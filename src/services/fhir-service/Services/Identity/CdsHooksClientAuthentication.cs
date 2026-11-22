using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Services.Identity;

/// <summary>
/// One EHR (CDS Client) CHO accepts CDS Hooks calls from.
///
/// <para>
/// CDS Hooks 2.0 §Trusting CDS Clients: the client signs a JWT per call, with
/// <c>iss</c> its own identifier, <c>aud</c> the CDS Service URL it is calling,
/// a unique <c>jti</c>, and keys it publishes as a JWK Set. The CDS Service
/// decides in advance which clients it trusts and where their keys live; a
/// token naming any other issuer, or pointing at any other key set, is refused.
/// </para>
/// </summary>
public sealed class CdsHooksTrustedClient
{
    /// <summary>The client's <c>iss</c>, exactly as it signs it.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Where the client's JWK Set is published. A token's <c>jku</c>, when present, must equal it.</summary>
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>The CHO tenant this client's calls act in. A CDS Hooks JWT carries no tenant of its own.</summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>Configuration section <c>CdsHooks</c>.</summary>
public sealed class CdsHooksOptions
{
    public const string SectionName = "CdsHooks";

    /// <summary>The CDS Clients CHO trusts. Empty (the default) means no CDS Hooks JWT is accepted.</summary>
    public List<CdsHooksTrustedClient> TrustedClients { get; set; } = [];

    /// <summary>
    /// CHO's public base URL for <c>/cds-services</c>, when it is served behind
    /// a proxy that rewrites the host. A token's <c>aud</c> may name the
    /// service under this base as well as under the URL the request arrived at.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>The longest lifetime CHO accepts for one call's token. CDS Hooks recommends five minutes.</summary>
    public TimeSpan MaxTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    internal CdsHooksTrustedClient? ClientFor(string? issuer)
        => issuer is null
            ? null
            : TrustedClients.FirstOrDefault(c => string.Equals(c.Issuer, issuer, StringComparison.Ordinal));

    /// <summary>Refuses a configuration that would trust a client without saying where its keys or tenant are.</summary>
    internal void Validate(bool isDevelopmentHost)
    {
        foreach (var client in TrustedClients)
        {
            if (string.IsNullOrWhiteSpace(client.Issuer)
                || string.IsNullOrWhiteSpace(client.TenantId)
                || !Uri.TryCreate(client.JwksUrl, UriKind.Absolute, out var jwks))
            {
                throw new SmartTrustValidationException(
                    "Every CdsHooks:TrustedClients entry needs an Issuer, an absolute JwksUrl and a TenantId.");
            }

            if (jwks.Scheme != Uri.UriSchemeHttps && !(isDevelopmentHost && jwks.Scheme == Uri.UriSchemeHttp))
            {
                throw new SmartTrustValidationException(
                    $"CdsHooks client '{client.Issuer}' publishes its keys over {jwks.Scheme}; only https is accepted "
                    + "outside a development host.");
            }
        }

        var duplicates = TrustedClients.GroupBy(c => c.Issuer, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new SmartTrustValidationException(
                $"CdsHooks:TrustedClients lists these issuers more than once: {string.Join(", ", duplicates)}.");
        }
    }
}

/// <summary>Authentication options for the CDS Hooks client scheme (all its settings live in <see cref="CdsHooksOptions"/>).</summary>
public sealed class CdsHooksClientSchemeOptions : AuthenticationSchemeOptions;

/// <summary>
/// Fetches and caches the JWK Sets of the configured CDS Clients. Only URLs
/// configuration named are ever fetched (the SSRF rule of
/// <see cref="JwksOriginPolicy"/>): the token chooses which configured client
/// it claims to be, never where its keys come from.
/// </summary>
public sealed class CdsHooksClientKeys
{
    public const string HttpClientName = "CdsHooksClientJwks";

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinRefetch = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<SecurityKey> Keys)> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public CdsHooksClientKeys(IHttpClientFactory http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    /// <summary>The client's keys; refetched when stale, or when <paramref name="kid"/> is not among them (a rotation).</summary>
    public async Task<IReadOnlyList<SecurityKey>> GetAsync(CdsHooksTrustedClient client, string? kid, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(client.JwksUrl, out var cached))
        {
            var fresh = now - cached.At < Fresh;
            var known = kid is null || cached.Keys.Any(k => k.KeyId == kid);
            if (fresh && (known || now - cached.At < MinRefetch))
                return cached.Keys;
        }

        using var response = await _http.CreateClient(HttpClientName).GetAsync(client.JwksUrl, ct);
        response.EnsureSuccessStatusCode();
        var set = new JsonWebKeySet(await response.Content.ReadAsStringAsync(ct));
        IReadOnlyList<SecurityKey> keys = set.GetSigningKeys().ToList();
        _cache[client.JwksUrl] = (now, keys);
        return keys;
    }
}

/// <summary>
/// The <c>jti</c>s already accepted, each kept until its token could no longer
/// validate. Its own cache, so a full shared cache can never evict one early.
/// </summary>
public sealed class CdsHooksReplayCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly object _gate = new();

    /// <summary>True the first time a key is seen; false for a replay.</summary>
    public bool TryRecord(string key, DateTimeOffset until)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out _))
                return false;
            _cache.Set(key, true, until);
            return true;
        }
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// Authenticates a CDS Client's per-call JWT (CDS Hooks 2.0). The principal
/// it yields names the client (<c>iss</c>, <c>sub</c>) and the tenant
/// configuration bound it to, and is marked as a CDS Hooks caller: it is
/// neither a CHO caller nor a SMART caller, and satisfies only endpoints that
/// admit CDS Hooks clients.
/// </summary>
public sealed class CdsHooksClientHandler : AuthenticationHandler<CdsHooksClientSchemeOptions>
{
    /// <summary>Algorithms CDS Hooks names for client JWTs.</summary>
    internal static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha512, SecurityAlgorithms.RsaSha512];

    private readonly IOptionsMonitor<CdsHooksOptions> _cds;
    private readonly CdsHooksClientKeys _keys;
    private readonly CdsHooksReplayCache _seen;

    public CdsHooksClientHandler(
        IOptionsMonitor<CdsHooksClientSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<CdsHooksOptions> cds,
        CdsHooksClientKeys keys,
        CdsHooksReplayCache seen)
        : base(options, logger, encoder)
    {
        _cds = cds;
        _keys = keys;
        _seen = seen;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var raw = header["Bearer ".Length..].Trim();
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(raw))
            return AuthenticateResult.Fail("The CDS Hooks token is not a JWT.");

        var unvalidated = handler.ReadJsonWebToken(raw);
        var options = _cds.CurrentValue;
        var client = options.ClientFor(unvalidated.Issuer);
        if (client is null)
            return AuthenticateResult.Fail("The CDS Hooks token's issuer is not a trusted CDS Client.");

        // jku is optional; when present it must be the key set configuration
        // names for this client, never somewhere the token chose.
        if (unvalidated.TryGetHeaderValue<string>("jku", out var jku)
            && !string.Equals(jku, client.JwksUrl, StringComparison.Ordinal))
        {
            Logger.LogWarning("CDS Hooks token from {Issuer} names a jku other than the configured one",
                LogSanitizer.Sanitize(client.Issuer));
            return AuthenticateResult.Fail("The CDS Hooks token's jku is not the client's configured key set.");
        }

        IReadOnlyList<SecurityKey> keys;
        try
        {
            keys = await _keys.GetAsync(client, unvalidated.Kid, Context.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            Logger.LogWarning(ex, "CDS Hooks client {Issuer} key set could not be read", LogSanitizer.Sanitize(client.Issuer));
            return AuthenticateResult.Fail("The CDS Client's key set could not be read.");
        }

        var result = await handler.ValidateTokenAsync(raw, new TokenValidationParameters
        {
            ValidIssuer = client.Issuer,
            ValidAudiences = Audiences(options),
            IssuerSigningKeys = keys,
            ValidAlgorithms = Algorithms,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
        });
        if (!result.IsValid)
        {
            Logger.LogWarning("CDS Hooks token from {Issuer} refused: {Reason}",
                LogSanitizer.Sanitize(client.Issuer), LogSanitizer.Sanitize(result.Exception?.Message ?? "invalid"));
            return AuthenticateResult.Fail("The CDS Hooks token is not valid.");
        }

        var token = (JsonWebToken)result.SecurityToken;
        if (token.ValidTo - (token.IssuedAt == DateTime.MinValue ? DateTime.UtcNow : token.IssuedAt) > options.MaxTokenLifetime + TimeSpan.FromSeconds(60))
            return AuthenticateResult.Fail("The CDS Hooks token lives longer than a single call needs.");

        // One token, one call: a replayed jti is refused for as long as the
        // token could still validate.
        if (string.IsNullOrEmpty(token.Id))
            return AuthenticateResult.Fail("The CDS Hooks token carries no jti.");
        var replayKey = $"cds-hooks-jti:{client.Issuer}:{token.Id}";
        if (!_seen.TryRecord(replayKey, new DateTimeOffset(token.ValidTo, TimeSpan.Zero).AddMinutes(1)))
            return AuthenticateResult.Fail("The CDS Hooks token has already been used.");

        var identity = new ClaimsIdentity(
        [
            new Claim("iss", client.Issuer),
            new Claim(ChoClaimTypes.Subject, string.IsNullOrEmpty(token.Subject) ? client.Issuer : token.Subject),
            new Claim(ChoClaimTypes.TenantId, client.TenantId),
        ], Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        FhirCallerSchemes.Mark(principal, FhirCallerSchemes.CdsHooksMarker);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>The CDS Service URL as the request reached CHO, and under the configured public base.</summary>
    private List<string> Audiences(CdsHooksOptions options)
    {
        var path = Request.PathBase.Add(Request.Path).Value ?? string.Empty;
        var audiences = new List<string> { $"{Request.Scheme}://{Request.Host}{path}" };
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            var basePath = options.PublicBaseUrl!.TrimEnd('/');
            var suffix = Request.Path.Value ?? string.Empty;
            audiences.Add(basePath.EndsWith("/cds-services", StringComparison.Ordinal) && suffix.StartsWith("/cds-services", StringComparison.Ordinal)
                ? basePath + suffix["/cds-services".Length..]
                : basePath + suffix);
        }

        return audiences;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { error = "A valid CDS Hooks client JWT is required." });
    }
}
