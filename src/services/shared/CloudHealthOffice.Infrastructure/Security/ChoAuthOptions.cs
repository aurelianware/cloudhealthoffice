using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Token trust for a CHO backend service, bound from the <c>ChoAuth</c>
/// configuration section.
///
/// <code>
/// "ChoAuth": {
///   "Audience": "cho-api",
///   "Issuers": [
///     { "Issuer": "cho-portal",   "PublicKeyPem": "..." },
///     { "Issuer": "cho-internal", "PublicKeyPem": "...", "AllowServiceRole": true }
///   ],
///   "ServiceToken": { "Issuer": "cho-internal", "ClientId": "claims-service", "PrivateKeyPem": "..." }
/// }
/// </code>
/// </summary>
public sealed class ChoAuthOptions
{
    public const string SectionName = "ChoAuth";

    /// <summary>The audience every CHO access token must carry.</summary>
    public string Audience { get; set; } = "cho-api";

    /// <summary>Issuers whose tokens this service accepts.</summary>
    public List<ChoTrustedIssuer> Issuers { get; set; } = new();

    /// <summary>
    /// How this service obtains a token for its own outbound calls when no user
    /// token is available to forward (message consumers, scheduled jobs).
    /// </summary>
    public ChoServiceTokenOptions? ServiceToken { get; set; }

    /// <summary>
    /// Host suffixes, beyond single-label and <c>*.svc[.cluster.local]</c>
    /// names, that count as CHO services for outbound token attachment.
    /// </summary>
    public List<string> InternalHostSuffixes { get; set; } = [".cloudhealthoffice"];

    /// <summary>Clock skew tolerated on token lifetime checks.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fails startup on a configuration that would accept forged or
    /// unaudienced tokens. Symmetric keys are a development convenience and are
    /// refused on any other host.
    /// </summary>
    public void Validate(bool allowSymmetricKeys)
    {
        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException($"{SectionName}:Audience is required.");

        if (Issuers.Count == 0)
            throw new InvalidOperationException(
                $"{SectionName}:Issuers is empty. Every CHO service requires authenticated callers; " +
                "configure at least one trusted issuer.");

        foreach (var issuer in Issuers)
        {
            if (string.IsNullOrWhiteSpace(issuer.Issuer))
                throw new InvalidOperationException($"{SectionName}:Issuers entry is missing 'Issuer'.");

            var sources = (string.IsNullOrWhiteSpace(issuer.PublicKeyPem) ? 0 : 1)
                        + (string.IsNullOrWhiteSpace(issuer.Authority) ? 0 : 1)
                        + (string.IsNullOrWhiteSpace(issuer.SymmetricKey) ? 0 : 1);
            if (sources != 1)
                throw new InvalidOperationException(
                    $"{SectionName} issuer '{issuer.Issuer}' must set exactly one of PublicKeyPem, Authority or SymmetricKey.");

            if (!string.IsNullOrWhiteSpace(issuer.SymmetricKey) && !allowSymmetricKeys)
                throw new InvalidOperationException(
                    $"{SectionName} issuer '{issuer.Issuer}' uses a symmetric key, which is permitted only on a Development host. " +
                    "Use an asymmetric key (PublicKeyPem) or an OIDC Authority.");
        }

        if (ServiceToken != null)
        {
            if (string.IsNullOrWhiteSpace(ServiceToken.Issuer) || string.IsNullOrWhiteSpace(ServiceToken.ClientId))
                throw new InvalidOperationException($"{SectionName}:ServiceToken requires Issuer and ClientId.");
            if (string.IsNullOrWhiteSpace(ServiceToken.PrivateKeyPem) == string.IsNullOrWhiteSpace(ServiceToken.SymmetricKey))
                throw new InvalidOperationException($"{SectionName}:ServiceToken must set exactly one of PrivateKeyPem or SymmetricKey.");
            if (!string.IsNullOrWhiteSpace(ServiceToken.SymmetricKey) && !allowSymmetricKeys)
                throw new InvalidOperationException(
                    $"{SectionName}:ServiceToken uses a symmetric key, which is permitted only on a Development host.");
        }
    }
}

/// <summary>An issuer a CHO service accepts tokens from.</summary>
public sealed class ChoTrustedIssuer
{
    /// <summary>Exact <c>iss</c> value.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>PEM-encoded RSA or EC public key that verifies this issuer's tokens.</summary>
    public string? PublicKeyPem { get; set; }

    /// <summary>OIDC authority whose discovery document supplies the signing keys.</summary>
    public string? Authority { get; set; }

    /// <summary>Base64 HMAC key. Development hosts only.</summary>
    public string? SymmetricKey { get; set; }

    /// <summary>
    /// Whether tokens from this issuer may carry the <see cref="ChoServiceRole"/>
    /// role. Only the internal service-token issuer should set this.
    /// </summary>
    public bool AllowServiceRole { get; set; }

    internal IEnumerable<SecurityKey> StaticKeys()
    {
        if (!string.IsNullOrWhiteSpace(SymmetricKey))
            return [new SymmetricSecurityKey(Convert.FromBase64String(SymmetricKey))];

        if (!string.IsNullOrWhiteSpace(PublicKeyPem))
            return [ChoKeys.PublicKeyFromPem(PublicKeyPem)];

        return [];
    }
}

/// <summary>Settings for minting this service's own access tokens.</summary>
public sealed class ChoServiceTokenOptions
{
    /// <summary>Issuer name written into minted tokens; must be trusted by callees.</summary>
    public string Issuer { get; set; } = "cho-internal";

    /// <summary>This service's identity (becomes <c>sub</c> and <c>azp</c>).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>PEM private key (RSA or EC). Load it from Key Vault, never from source.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Base64 HMAC key. Development hosts only.</summary>
    public string? SymmetricKey { get; set; }

    /// <summary>Lifetime of a minted token.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
}

internal static class ChoKeys
{
    internal static SecurityKey PublicKeyFromPem(string pem)
    {
        if (TryImportEc(pem, out var ec))
            return new ECDsaSecurityKey(ec);

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }

    internal static SigningCredentials SigningCredentialsFrom(string? privateKeyPem, string? symmetricKey)
    {
        if (!string.IsNullOrWhiteSpace(symmetricKey))
            return new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(symmetricKey)), SecurityAlgorithms.HmacSha256);

        if (string.IsNullOrWhiteSpace(privateKeyPem))
            throw new InvalidOperationException("A signing key is required.");

        if (TryImportEc(privateKeyPem, out var ec))
            return new SigningCredentials(new ECDsaSecurityKey(ec), SecurityAlgorithms.EcdsaSha256);

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
    }

    private static bool TryImportEc(string pem, out ECDsa ec)
    {
        ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ec.Dispose();
            ec = null!;
            return false;
        }
    }
}
