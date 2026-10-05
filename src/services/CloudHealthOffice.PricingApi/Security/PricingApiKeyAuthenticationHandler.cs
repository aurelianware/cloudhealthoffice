using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.PricingApi.Security;

/// <summary>
/// The external-caller scheme: Pricing API customers (health plans, TPAs,
/// clearinghouses on the public ingress) hold no CHO token. They authenticate
/// with the API key issued to them in <c>X-API-Key</c>. It is a scheme of its
/// own, never the CHO "Bearer" scheme.
///
/// The principal it produces is bound to the credential: its subject and its
/// <c>tenant_id</c> are both <c>pricing-api-key:&lt;key id&gt;</c>, a
/// namespace no CHO tenant uses. The shared tenant middleware therefore takes
/// the tenant from the credential exactly as it does from a CHO token (a
/// disagreeing <c>X-Tenant-ID</c> is refused with 403), and a customer can never
/// act in a CHO tenant. The principal carries no roles and no permissions, so it
/// satisfies no CHO permission; only <see cref="PricingApiAuth.CallerPolicy"/>
/// admits it.
/// </summary>
public sealed class PricingApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PricingApiKey";
    public const string ApiKeyHeader = "X-API-Key";
    public const string CustomerClaim = "pricing_customer";
    public const string CredentialPrefix = "pricing-api-key:";

    /// <summary>The authenticated key's record, for usage metering and quota checks.</summary>
    public const string ApiKeyRecordItem = "ApiKeyRecord";

    private readonly IApiKeyRepository _apiKeys;

    public PricingApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var values))
            return AuthenticateResult.NoResult();

        // One credential per request. A request that also carries a bearer
        // token states two identities; neither is chosen for it.
        if (!string.IsNullOrEmpty(Request.Headers.Authorization.ToString()))
        {
            Logger.LogWarning("Pricing API request rejected: both an API key and an Authorization header were sent");
            return AuthenticateResult.Fail("Both an API key and a bearer token were presented.");
        }

        var supplied = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(supplied))
        {
            Logger.LogWarning("Pricing API request rejected: empty or repeated API key header");
            return AuthenticateResult.Fail("Invalid API key.");
        }

        // Only the hash is stored; the lookup is by hash.
        var suppliedHash = ApiKeyHashing.Hash(supplied);
        var record = await _apiKeys.GetByHashAsync(suppliedHash);
        // The stored hash must match exactly (the lookup may be collation-insensitive).
        if (record is null || !record.IsActive || string.IsNullOrEmpty(record.KeyId) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(record.KeyHash ?? string.Empty), Encoding.ASCII.GetBytes(suppliedHash)))
        {
            // Never log any part of the supplied key.
            Logger.LogWarning("Pricing API request rejected: unknown or deactivated API key");
            return AuthenticateResult.Fail("Invalid API key.");
        }

        // The key id: stable per key, public, and reveals nothing of the key.
        var credentialId = CredentialPrefix + record.KeyId;
        var identity = new ClaimsIdentity(
            [
                new Claim(ChoClaimTypes.Subject, credentialId),
                new Claim(ChoClaimTypes.TenantId, credentialId),
                new Claim(ChoClaimTypes.Name, record.TenantName),
                new Claim(CustomerClaim, record.TenantName),
            ],
            SchemeName,
            ChoClaimTypes.Name,
            ChoClaimTypes.Role);

        Context.Items[ApiKeyRecordItem] = record;

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
