using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.SmartBackend;

/// <summary>Why no access token came back.</summary>
public enum SmartBackendTokenFailure
{
    None,

    /// <summary>No registration under that key, or no signing key: CHO holds no credential for the server.</summary>
    NotConfigured,

    /// <summary>The token endpoint refused CHO's client or the requested scopes.</summary>
    Rejected,

    /// <summary>The token endpoint could not be reached, timed out, or failed server-side.</summary>
    Unavailable,

    /// <summary>The token endpoint answered with something that is not a usable bearer token.</summary>
    InvalidResponse,
}

/// <summary>An access token from a SMART Backend Services token endpoint, or why there is none.</summary>
public sealed record SmartBackendTokenResult(string? AccessToken, DateTimeOffset ExpiresAt, SmartBackendTokenFailure Failure)
{
    public bool Succeeded => Failure == SmartBackendTokenFailure.None && AccessToken is not null;

    public static SmartBackendTokenResult Failed(SmartBackendTokenFailure failure) => new(null, DateTimeOffset.MinValue, failure);
}

/// <summary>
/// CHO as a SMART Backend Services client (G-42 outbound): gets
/// <c>client_credentials</c> access tokens from another server by presenting
/// a <c>private_key_jwt</c> client assertion signed with CHO's key, and caches
/// them until shortly before they expire.
///
/// Callers name a registration (<see cref="SmartBackendClientOptions.Clients"/>),
/// never a URL, so the only hosts this ever calls are configured token
/// endpoints. Payer-to-Payer uses it through
/// <see cref="PayerToPayer.Outbound.SmartBackendPayerToPayerCredentialProvider"/>;
/// any other outbound FHIR client (bulk <c>$export</c> against another payer,
/// for one) can use it directly.
/// </summary>
public interface ISmartBackendTokenClient
{
    /// <summary>A valid access token for <paramref name="clientKey"/>, from cache when one is still fresh.</summary>
    Task<SmartBackendTokenResult> GetTokenAsync(string clientKey, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached token for <paramref name="clientKey"/>, e.g. after the
    /// resource server answered 401 with it, so the next call gets a new one.
    /// </summary>
    void Invalidate(string clientKey);
}

/// <inheritdoc />
public sealed class SmartBackendTokenClient : ISmartBackendTokenClient
{
    public const string HttpClientName = "SmartBackendToken";
    internal const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>When a server omits <c>expires_in</c>: SMART recommends five minutes or less.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private const int MaxResponseBytes = 64 * 1024;

    private readonly IHttpClientFactory _clientFactory;
    private readonly SmartBackendKeyRing _keys;
    private readonly IOptions<SmartBackendClientOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SmartBackendTokenClient> _logger;
    private readonly JsonWebTokenHandler _tokens = new() { SetDefaultTimesOnTokenCreation = false };

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private sealed record Cached(string AccessToken, DateTimeOffset ExpiresAt, DateTimeOffset RefreshAt);

    public SmartBackendTokenClient(
        IHttpClientFactory clientFactory,
        SmartBackendKeyRing keys,
        IOptions<SmartBackendClientOptions> options,
        TimeProvider time,
        ILogger<SmartBackendTokenClient> logger)
    {
        _clientFactory = clientFactory;
        _keys = keys;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<SmartBackendTokenResult> GetTokenAsync(string clientKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientKey)
            || !_options.Value.Clients.TryGetValue(clientKey, out var registration)
            || _keys.Active is null)
            return SmartBackendTokenResult.Failed(SmartBackendTokenFailure.NotConfigured);

        if (TryFresh(clientKey, out var hit)) return hit;

        // One token request per registration at a time: concurrent callers wait
        // for it rather than each signing an assertion and asking again.
        var gate = _gates.GetOrAdd(clientKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryFresh(clientKey, out hit)) return hit;

            if (!TryTokenEndpoint(registration.TokenEndpoint, out var tokenEndpoint) || string.IsNullOrWhiteSpace(registration.ClientId))
            {
                _logger.LogWarning(
                    "SMART backend registration {ClientKey} has no usable client id or token endpoint; no token requested.",
                    LogSanitizer.Sanitize(clientKey));
                return SmartBackendTokenResult.Failed(SmartBackendTokenFailure.NotConfigured);
            }

            var result = await RequestAsync(clientKey, registration, tokenEndpoint, ct);
            if (result.Succeeded)
            {
                var now = _time.GetUtcNow();
                var lifetime = result.ExpiresAt - now;
                var skew = TimeSpan.FromSeconds(Math.Max(0, _options.Value.RefreshSkewSeconds));
                // A token that lives less than twice the skew is refreshed at half-life instead.
                var refreshAt = lifetime > skew * 2 ? result.ExpiresAt - skew : now + lifetime / 2;
                _cache[clientKey] = new Cached(result.AccessToken!, result.ExpiresAt, refreshAt);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string clientKey)
    {
        if (!string.IsNullOrEmpty(clientKey)) _cache.TryRemove(clientKey, out _);
    }

    private bool TryFresh(string clientKey, out SmartBackendTokenResult result)
    {
        if (_cache.TryGetValue(clientKey, out var cached) && _time.GetUtcNow() < cached.RefreshAt)
        {
            result = new SmartBackendTokenResult(cached.AccessToken, cached.ExpiresAt, SmartBackendTokenFailure.None);
            return true;
        }
        result = null!;
        return false;
    }

    private async Task<SmartBackendTokenResult> RequestAsync(
        string clientKey, SmartBackendRegistrationOptions registration, Uri tokenEndpoint, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = ClientAssertionType,
            ["client_assertion"] = CreateAssertion(registration.ClientId.Trim(), tokenEndpoint),
        };
        if (!string.IsNullOrWhiteSpace(registration.Scope)) form["scope"] = registration.Scope.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _clientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                "SMART backend token request for {ClientKey} was unreachable ({Fault}).",
                LogSanitizer.Sanitize(clientKey), ex.GetType().Name);
            return SmartBackendTokenResult.Failed(SmartBackendTokenFailure.Unavailable);
        }

        using (response)
        {
            var body = await ReadCappedAsync(response, ct);
            if (!response.IsSuccessStatusCode)
            {
                var failure = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                    ? SmartBackendTokenFailure.Unavailable
                    : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? SmartBackendTokenFailure.Rejected
                        : SmartBackendTokenFailure.InvalidResponse;
                // The OAuth error code is a fixed vocabulary and safe to log; the
                // description is free text from the peer and is not.
                _logger.LogWarning(
                    "SMART backend token request for {ClientKey} returned {Status} ({Error}) → {Failure}.",
                    LogSanitizer.Sanitize(clientKey), (int)response.StatusCode,
                    LogSanitizer.Sanitize(ErrorCode(body) ?? "-"), failure);
                return SmartBackendTokenResult.Failed(failure);
            }

            var parsed = ParseToken(body);
            if (parsed is null)
            {
                _logger.LogWarning(
                    "SMART backend token response for {ClientKey} carried no usable bearer token.",
                    LogSanitizer.Sanitize(clientKey));
                return SmartBackendTokenResult.Failed(SmartBackendTokenFailure.InvalidResponse);
            }
            return parsed;
        }
    }

    /// <summary>
    /// The SMART Backend Services client assertion: <c>iss</c> = <c>sub</c> =
    /// client id, <c>aud</c> = the token endpoint, a fresh <c>jti</c>, and
    /// <c>exp</c> no more than five minutes out; header <c>typ</c> JWT and the
    /// signing key's <c>kid</c>.
    /// </summary>
    internal string CreateAssertion(string clientId, Uri tokenEndpoint)
    {
        var key = _keys.Active!;
        var now = _time.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(_options.Value.AssertionLifetimeSeconds, 30, 300));

        return _tokens.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = tokenEndpoint.AbsoluteUri,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            }),
            IssuedAt = now,
            Expires = now + lifetime,
            TokenType = "JWT",
            SigningCredentials = new SigningCredentials(key.Key, key.Algorithm),
        });
    }

    private SmartBackendTokenResult? ParseToken(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
                return null;
            if (!root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "bearer", StringComparison.OrdinalIgnoreCase))
                return null;

            var lifetime = DefaultLifetime;
            if (root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.ValueKind == JsonValueKind.Number
                && expiresIn.TryGetInt64(out var seconds))
            {
                if (seconds <= 0) return null;
                lifetime = TimeSpan.FromSeconds(Math.Min(seconds, (long)TimeSpan.FromDays(1).TotalSeconds));
            }

            return new SmartBackendTokenResult(token.GetString(), _time.GetUtcNow() + lifetime, SmartBackendTokenFailure.None);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorCode(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                ? error.GetString() is { Length: <= 64 } code ? code : null
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Same rules as the Payer-to-Payer directory: absolute HTTPS, no user info, query or fragment.</summary>
    private bool TryTokenEndpoint(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        var https = parsed.Scheme == Uri.UriSchemeHttps;
        var http = parsed.Scheme == Uri.UriSchemeHttp && _options.Value.AllowInsecureTransport;
        if (!https && !http) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = parsed;
        return true;
    }
}
