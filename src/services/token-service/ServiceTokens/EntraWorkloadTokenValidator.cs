using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.ServiceTokens;

/// <summary>The Azure identity behind a validated app-only Entra token.</summary>
public sealed record EntraWorkloadIdentity(Guid ObjectId, string? AppId);

public sealed record EntraWorkloadTokenResult(EntraWorkloadIdentity? Identity, string? Reason)
{
    public static EntraWorkloadTokenResult Refused(string reason) => new(null, reason);
}

public interface IEntraWorkloadTokenValidator
{
    Task<EntraWorkloadTokenResult> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>Entra's token signing keys.</summary>
public interface IEntraSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct);
}

/// <summary>
/// Validates an app-only Entra access token that a CHO service obtained with
/// its workload identity (managed identity) for token-service's service-token
/// API. Checked locally: signature (Entra's published keys), <c>iss</c> = CHO's
/// own directory (v1 or v2 form), <c>tid</c> = that directory, <c>aud</c> = the
/// configured API, expiry, and that the token is app-only (no <c>scp</c>,
/// <c>sub</c> = <c>oid</c>, <c>idtyp</c> = app when present), plus the
/// required app role when configured. User (delegated) tokens are refused.
/// </summary>
public sealed class EntraWorkloadTokenValidator : IEntraWorkloadTokenValidator
{
    private const int MaxTokenLength = 16 * 1024;

    private readonly ServiceTokenOptions _options;
    private readonly IEntraSigningKeys _keys;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public EntraWorkloadTokenValidator(ServiceTokenOptions options, IEntraSigningKeys keys)
    {
        _options = options;
        _keys = keys;
    }

    public async Task<EntraWorkloadTokenResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return EntraWorkloadTokenResult.Refused("malformed_token");

        var result = await ValidateSignedAsync(token, refresh: false, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Entra rolls its keys; the cached set may predate this one.
            result = await ValidateSignedAsync(token, refresh: true, ct);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return EntraWorkloadTokenResult.Refused(Reason(result.Exception));

        if (!jwt.TryGetPayloadValue<string>("tid", out var tid)
            || !Guid.TryParse(tid, out var tidGuid)
            || tidGuid != Guid.Parse(_options.EntraTenantId))
            return EntraWorkloadTokenResult.Refused("wrong_directory");

        // App-only: a managed identity's token. A user's delegated token
        // carries scp; an app token's subject is its own service principal.
        if (jwt.TryGetPayloadValue<string>("scp", out var scp) && !string.IsNullOrEmpty(scp))
            return EntraWorkloadTokenResult.Refused("delegated_token");
        if (jwt.TryGetPayloadValue<string>("idtyp", out var idtyp) && !string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase))
            return EntraWorkloadTokenResult.Refused("not_app_token");
        if (!jwt.TryGetPayloadValue<string>("oid", out var oid) || !Guid.TryParse(oid, out var oidGuid))
            return EntraWorkloadTokenResult.Refused("no_oid");
        if (!Guid.TryParse(jwt.Subject, out var subGuid) || subGuid != oidGuid)
            return EntraWorkloadTokenResult.Refused("not_app_token");

        if (!string.IsNullOrWhiteSpace(_options.RequiredAppRole)
            && !jwt.Claims.Any(c => c.Type == "roles" && string.Equals(c.Value, _options.RequiredAppRole, StringComparison.Ordinal)))
            return EntraWorkloadTokenResult.Refused("missing_app_role");

        // v1 tokens name the calling application in appid, v2 in azp.
        var appId = jwt.TryGetPayloadValue<string>("azp", out var azp) && !string.IsNullOrEmpty(azp) ? azp
            : jwt.TryGetPayloadValue<string>("appid", out var appid) && !string.IsNullOrEmpty(appid) ? appid
            : null;

        return new EntraWorkloadTokenResult(new EntraWorkloadIdentity(oidGuid, appId), null);
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
            ValidIssuers = _options.EntraIssuers,
            ValidateAudience = true,
            ValidAudiences = _options.Audiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = _options.ClockSkew,
        });
    }

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
        : Exception("Entra's signing keys could not be loaded.", inner);
}

/// <summary>A configured JWKS.</summary>
public sealed class StaticEntraSigningKeys : IEntraSigningKeys
{
    private readonly IReadOnlyCollection<SecurityKey> _keys;

    public StaticEntraSigningKeys(string jwksJson) => _keys = new JsonWebKeySet(jwksJson).GetSigningKeys().ToArray();

    public Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct) => Task.FromResult(_keys);
}

/// <summary>Entra's keys from its OIDC discovery document, cached and refreshed by the configuration manager.</summary>
public sealed class DiscoveryEntraSigningKeys : IEntraSigningKeys
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;

    public DiscoveryEntraSigningKeys(string metadataAddress, HttpClient httpClient, bool requireHttps)
        => _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(httpClient) { RequireHttps = requireHttps });

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh)
            _manager.RequestRefresh();
        var configuration = await _manager.GetConfigurationAsync(ct);
        return configuration.SigningKeys.ToArray();
    }
}
