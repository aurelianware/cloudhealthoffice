using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Workload;

/// <summary>The Kubernetes identity behind a validated projected service-account token.</summary>
public sealed record KubernetesIdentity(string Namespace, string ServiceAccount, string? PodName);

/// <summary>Outcome of validating a service-account token: an identity, or a refusal reason.</summary>
public sealed record KubernetesTokenResult(KubernetesIdentity? Identity, string? Reason)
{
    public static KubernetesTokenResult Refused(string reason) => new(null, reason);
}

public interface IKubernetesTokenValidator
{
    Task<KubernetesTokenResult> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>Signing keys of the cluster's service-account issuer.</summary>
public interface IKubernetesSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct);
}

/// <summary>
/// Validates a projected Kubernetes service-account token locally, against the
/// cluster's service-account issuer and its published keys (OIDC discovery, as
/// AKS workload identity uses, or a configured JWKS). Checked: signature,
/// <c>iss</c> = the configured issuer, <c>aud</c> = token-service's audience,
/// expiry, a short lifetime, and a pod-bound <c>kubernetes.io</c> claim that
/// agrees with <c>sub</c> (<c>system:serviceaccount:&lt;ns&gt;:&lt;name&gt;</c>).
/// </summary>
public sealed class KubernetesTokenValidator : IKubernetesTokenValidator
{
    private const int MaxTokenLength = 16 * 1024;
    private const string SubjectPrefix = "system:serviceaccount:";

    private readonly WorkloadTokenOptions _options;
    private readonly IKubernetesSigningKeys _keys;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public KubernetesTokenValidator(WorkloadTokenOptions options, IKubernetesSigningKeys keys, TimeProvider? time = null)
    {
        _options = options;
        _keys = keys;
        _time = time ?? TimeProvider.System;
    }

    public async Task<KubernetesTokenResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return KubernetesTokenResult.Refused("malformed_token");

        var result = await ValidateSignedAsync(token, refresh: false, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The cluster may have rotated its key since the keys were cached.
            result = await ValidateSignedAsync(token, refresh: true, ct);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return KubernetesTokenResult.Refused(Reason(result.Exception));

        if (jwt.IssuedAt == DateTime.MinValue)
            return KubernetesTokenResult.Refused("no_iat");
        if (jwt.ValidTo - jwt.IssuedAt > _options.MaxSourceTokenLifetime)
            return KubernetesTokenResult.Refused("token_lifetime_too_long");

        var subject = jwt.Subject;
        if (string.IsNullOrEmpty(subject) || !subject.StartsWith(SubjectPrefix, StringComparison.Ordinal))
            return KubernetesTokenResult.Refused("not_a_service_account");
        var parts = subject[SubjectPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
            return KubernetesTokenResult.Refused("not_a_service_account");

        // Projected tokens carry the pod binding. Legacy secret-based tokens do
        // not (and have a different issuer and no audience).
        string? claimNamespace = null, claimAccount = null, podName = null;
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
            if (payload.RootElement.TryGetProperty("kubernetes.io", out var k8s) && k8s.ValueKind == JsonValueKind.Object)
            {
                claimNamespace = Str(k8s, "namespace");
                if (k8s.TryGetProperty("serviceaccount", out var sa) && sa.ValueKind == JsonValueKind.Object)
                    claimAccount = Str(sa, "name");
                if (k8s.TryGetProperty("pod", out var pod) && pod.ValueKind == JsonValueKind.Object)
                    podName = Str(pod, "name");
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return KubernetesTokenResult.Refused("malformed_token");
        }

        if (claimNamespace == null || claimAccount == null)
            return KubernetesTokenResult.Refused("not_a_projected_token");
        if (!string.Equals(claimNamespace, parts[0], StringComparison.Ordinal)
            || !string.Equals(claimAccount, parts[1], StringComparison.Ordinal))
            return KubernetesTokenResult.Refused("subject_mismatch");
        if (podName == null)
            return KubernetesTokenResult.Refused("not_pod_bound");

        return new KubernetesTokenResult(new KubernetesIdentity(parts[0], parts[1], podName), null);
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
            ValidIssuer = _options.KubernetesIssuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
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

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    private static string Reason(Exception? ex) => ex switch
    {
        KeysUnavailableException => "issuer_keys_unavailable",
        SecurityTokenInvalidAudienceException => "wrong_audience",
        SecurityTokenInvalidIssuerException => "wrong_issuer",
        SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException or SecurityTokenNotYetValidException => "expired",
        SecurityTokenNoExpirationException => "no_expiry",
        SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "bad_signature",
        SecurityTokenInvalidAlgorithmException => "bad_algorithm",
        _ => "invalid_token",
    };

    public sealed class KeysUnavailableException(Exception inner)
        : Exception("The cluster's service-account signing keys could not be loaded.", inner);
}

/// <summary>A JWKS from configuration.</summary>
public sealed class StaticKubernetesSigningKeys : IKubernetesSigningKeys
{
    private readonly IReadOnlyCollection<SecurityKey> _keys;

    public StaticKubernetesSigningKeys(string jwksJson) => _keys = new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();

    public Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct) => Task.FromResult(_keys);
}

/// <summary>
/// Keys from the issuer's OIDC discovery document (AKS: the public OIDC issuer
/// that workload identity federation uses). Cached and refreshed by the
/// configuration manager; a forced refresh is rate limited by it.
/// </summary>
public sealed class DiscoveryKubernetesSigningKeys : IKubernetesSigningKeys
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;

    public DiscoveryKubernetesSigningKeys(WorkloadTokenOptions options, HttpClient httpClient, bool requireHttps)
    {
        var metadata = string.IsNullOrWhiteSpace(options.MetadataAddress)
            ? options.KubernetesIssuer.TrimEnd('/') + "/.well-known/openid-configuration"
            : options.MetadataAddress!;
        _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(httpClient) { RequireHttps = requireHttps });
    }

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh)
            _manager.RequestRefresh();
        var configuration = await _manager.GetConfigurationAsync(ct);
        return configuration.SigningKeys.ToArray();
    }
}
