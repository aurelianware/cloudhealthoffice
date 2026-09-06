using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.Identity;

/// <summary>
/// The token validation rules, in one place.
///
/// Extracted from the DI wiring so that the rules the tests assert are
/// literally the rules the server runs. A test that rebuilt an equivalent
/// TokenValidationParameters would be testing its own copy, and the copy is
/// exactly what drifts.
/// </summary>
public static class SmartTokenValidation
{
    public static TokenValidationParameters CreateParameters(
        TrustedIssuerRegistry registry, SmartSigningKeyRing keyRing)
    {
        // A coarse outer bound across issuers; ValidateIssuerPolicy below makes
        // the per-issuer assertion. Both exist so an edit to either cannot
        // silently widen what the handler will consider.
        var allAlgorithms = registry.Issuers
            .SelectMany(i => i.EffectiveAlgorithms())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new TokenValidationParameters
        {
            // An alg=none token carries no signature, so this is what refuses
            // it. Stated rather than left to the default, being the single most
            // consequential default here.
            RequireSignedTokens = true,
            RequireExpirationTime = true,

            ValidateIssuer = true,
            ValidIssuers = registry.IssuerNames,

            ValidateLifetime = true,
            ClockSkew = registry.ClockSkew,

            ValidateIssuerSigningKey = true,
            ValidAlgorithms = allAlgorithms,

            // Audience is checked against the issuer that actually signed this
            // token, so one trusted IdP's audiences never authorize another's.
            ValidateAudience = true,
            AudienceValidator = (audiences, securityToken, _) =>
            {
                var issuer = registry.Resolve(securityToken?.Issuer);
                return issuer != null
                    && audiences?.Any(a => issuer.Audiences.Contains(a, StringComparer.Ordinal)) == true;
            },

            // Per-issuer keys. Returning none is a hard failure — the handler
            // has no other key source to fall back to, which is the intent.
            IssuerSigningKeyResolver = (_, securityToken, kid, _) =>
                keyRing.ResolveKeys(securityToken?.Issuer, kid),
        };
    }

    /// <summary>
    /// Per-issuer algorithm enforcement, applied after the handler has
    /// validated the signature.
    ///
    /// <see cref="TokenValidationParameters.ValidAlgorithms"/> is the union
    /// across every trusted issuer, so without this an issuer restricted to
    /// ES256 would still accept an RS256 token signed by its own keys.
    /// </summary>
    public static bool AlgorithmIsAcceptedForIssuer(
        TrustedIssuerRegistry registry, SecurityToken? token, string issuerName)
    {
        var issuer = registry.Resolve(issuerName);
        if (issuer == null) return false;
        if (token is not JsonWebToken jwt) return true;

        return issuer.EffectiveAlgorithms().Contains(jwt.Alg, StringComparer.Ordinal);
    }
}
