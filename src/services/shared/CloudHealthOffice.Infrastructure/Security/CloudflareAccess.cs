using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Trust settings for Cloudflare Access application tokens. When an
/// application sits behind Cloudflare Access, every request Access lets through
/// carries the signed-in person's Access token in the
/// <c>Cf-Access-Jwt-Assertion</c> header: an RS256 JWT whose issuer is the
/// Zero Trust team domain and whose audience is the Access application's AUD tag.
/// </summary>
public sealed class CloudflareAccessOptions
{
    /// <summary>The request header Cloudflare Access adds to every proxied request.</summary>
    public const string AssertionHeader = "Cf-Access-Jwt-Assertion";

    /// <summary>
    /// The Zero Trust team domain, e.g. <c>cloudhealthoffice.cloudflareaccess.com</c>
    /// (with or without <c>https://</c>). The token's issuer must be exactly
    /// <c>https://&lt;team domain&gt;</c>, and its keys come from
    /// <c>https://&lt;team domain&gt;/cdn-cgi/access/certs</c>.
    /// </summary>
    public string TeamDomain { get; set; } = string.Empty;

    /// <summary>
    /// The Access application AUD tags accepted (Zero Trust → Access →
    /// Applications → the application → Overview → Application Audience (AUD) Tag).
    /// </summary>
    public List<string> Audiences { get; set; } = new();

    /// <summary>Optional: the team's JWKS, instead of fetching the certs URL (tests, air-gapped).</summary>
    public string? JwksJson { get; set; }

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary><c>https://&lt;team domain&gt;</c>, the only issuer accepted.</summary>
    public string Issuer => "https://" + Host();

    /// <summary>Where Cloudflare publishes the team's signing keys.</summary>
    public string CertsUrl => Issuer + "/cdn-cgi/access/certs";

    public void Validate(string section)
    {
        if (string.IsNullOrWhiteSpace(TeamDomain))
            throw new InvalidOperationException($"{section}:TeamDomain is required (e.g. yourteam.cloudflareaccess.com).");
        _ = Host(section);
        if (Audiences.Count == 0 || Audiences.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                $"{section}:Audiences must list the Access application's AUD tag.");
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException($"{section}:ClockSkew must be between 0 and 5 minutes.");
    }

    private string Host(string section = "CloudflareAccess")
    {
        var value = TeamDomain.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Trim('/').Length > 0
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"{section}:TeamDomain must be a host name such as yourteam.cloudflareaccess.com (https, no path).");
        }
        return uri.IdnHost.ToLowerInvariant();
    }
}

/// <summary>The person Cloudflare Access signed in.</summary>
public sealed record CloudflareAccessIdentity(string Email, string Subject, string Issuer);

public sealed record CloudflareAccessResult(CloudflareAccessIdentity? Identity, string? Reason)
{
    public static CloudflareAccessResult Refused(string reason) => new(null, reason);
}

/// <summary>Signing keys of a Cloudflare Zero Trust team.</summary>
public interface ICloudflareAccessKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct);
}

public interface ICloudflareAccessTokenValidator
{
    Task<CloudflareAccessResult> ValidateAsync(string? token, CancellationToken ct);
}

/// <summary>
/// Validates a Cloudflare Access application token: RS256 signature by the
/// team's keys, issuer = the team domain, audience = a configured AUD tag,
/// expiry, and a person (an <c>email</c> and <c>sub</c>). Access service-token
/// JWTs (machine clients, <c>common_name</c>, no email) are refused: they name
/// no person to sign in.
/// </summary>
public sealed class CloudflareAccessTokenValidator : ICloudflareAccessTokenValidator
{
    private const int MaxTokenLength = 16 * 1024;

    private readonly CloudflareAccessOptions _options;
    private readonly ICloudflareAccessKeys _keys;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public CloudflareAccessTokenValidator(CloudflareAccessOptions options, ICloudflareAccessKeys keys, TimeProvider? time = null)
    {
        _options = options;
        _keys = keys;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CloudflareAccessResult> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return CloudflareAccessResult.Refused("malformed_token");

        var result = await ValidateSignedAsync(token, refresh: false, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Cloudflare rotates the team's keys; refetch once.
            result = await ValidateSignedAsync(token, refresh: true, ct);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return CloudflareAccessResult.Refused(Reason(result.Exception));

        if (jwt.TryGetPayloadValue<string>("common_name", out var commonName) && !string.IsNullOrEmpty(commonName))
            return CloudflareAccessResult.Refused("service_token");

        var email = jwt.TryGetPayloadValue<string>("email", out var e) ? e?.Trim() : null;
        if (string.IsNullOrEmpty(email) || !email.Contains('@', StringComparison.Ordinal))
            return CloudflareAccessResult.Refused("no_email");
        if (string.IsNullOrWhiteSpace(jwt.Subject))
            return CloudflareAccessResult.Refused("no_subject");

        return new CloudflareAccessResult(new CloudflareAccessIdentity(email, jwt.Subject, jwt.Issuer), null);
    }

    private async Task<TokenValidationResult> ValidateSignedAsync(string token, bool refresh, CancellationToken ct)
    {
        IReadOnlyCollection<SecurityKey> keys;
        try
        {
            keys = await _keys.GetAsync(refresh, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TokenValidationResult { IsValid = false, Exception = new KeysUnavailableException(ex) };
        }

        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudiences = _options.Audiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = _options.ClockSkew,
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = _time.GetUtcNow().UtcDateTime;
                return expires.HasValue
                       && expires.Value.ToUniversalTime() + p.ClockSkew > now
                       && (!notBefore.HasValue || notBefore.Value.ToUniversalTime() - p.ClockSkew <= now);
            },
        });
    }

    private static string Reason(Exception? ex) => ex switch
    {
        KeysUnavailableException => "access_keys_unavailable",
        SecurityTokenInvalidAudienceException => "wrong_audience",
        SecurityTokenInvalidIssuerException => "wrong_issuer",
        SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException or SecurityTokenNotYetValidException => "expired",
        SecurityTokenNoExpirationException => "no_expiry",
        SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "bad_signature",
        SecurityTokenInvalidAlgorithmException => "bad_algorithm",
        _ => "invalid_token",
    };

    public sealed class KeysUnavailableException(Exception inner)
        : Exception("The Cloudflare Access signing keys could not be loaded.", inner);

    /// <summary>The validator for these options: a configured JWKS, or the team's certs URL.</summary>
    public static CloudflareAccessTokenValidator Create(CloudflareAccessOptions options, HttpClient httpClient, TimeProvider? time = null)
        => new(options,
            string.IsNullOrWhiteSpace(options.JwksJson)
                ? new RemoteCloudflareAccessKeys(options.CertsUrl, httpClient)
                : new StaticCloudflareAccessKeys(options.JwksJson!),
            time);
}

/// <summary>A JWKS from configuration.</summary>
public sealed class StaticCloudflareAccessKeys : ICloudflareAccessKeys
{
    private readonly IReadOnlyCollection<SecurityKey> _keys;

    public StaticCloudflareAccessKeys(string jwksJson) => _keys = new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();

    public Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct) => Task.FromResult(_keys);
}

/// <summary>
/// The team's keys from <c>/cdn-cgi/access/certs</c> (a JWKS with extra
/// certificate fields, which are ignored). Cached and refreshed by the
/// configuration manager; a forced refresh is rate limited by it.
/// </summary>
public sealed class RemoteCloudflareAccessKeys : ICloudflareAccessKeys
{
    private readonly ConfigurationManager<JsonWebKeySet> _manager;

    public RemoteCloudflareAccessKeys(string certsUrl, HttpClient httpClient)
        => _manager = new ConfigurationManager<JsonWebKeySet>(
            certsUrl, new JwksRetriever(), new HttpDocumentRetriever(httpClient) { RequireHttps = true });

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh)
            _manager.RequestRefresh();
        var set = await _manager.GetConfigurationAsync(ct);
        return set.GetSigningKeys().ToArray();
    }

    private sealed class JwksRetriever : IConfigurationRetriever<JsonWebKeySet>
    {
        public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
            => new(await retriever.GetDocumentAsync(address, cancel));
    }
}
