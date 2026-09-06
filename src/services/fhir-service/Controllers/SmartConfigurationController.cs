using FhirService.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Serves the SMART App Launch Framework v2 well-known configuration document.
///
/// Per SMART IG §3.1, this endpoint MUST be at {fhir-base}/.well-known/smart-configuration
/// so that SMART clients can discover auth server endpoints from just the FHIR base URL.
///
/// https://hl7.org/fhir/smart-app-launch/conformance.html
/// </summary>
[Route("fhir/r4")]
public class SmartConfigurationController : FhirControllerBase
{
    private readonly IConfiguration _config;
    private readonly TrustedIssuerRegistry? _registry;
    private readonly SmartSigningKeyRing? _keyRing;

    /// <summary>
    /// The registry and key ring are optional so a Demo-mode host — and the
    /// acceptance suite — can construct this controller from configuration
    /// alone. When they are present and the deployment trusts an external
    /// issuer, the advertised endpoints come from that issuer's own discovery
    /// document instead of being synthesized.
    /// </summary>
    public SmartConfigurationController(
        IConfiguration config,
        TrustedIssuerRegistry? registry = null,
        SmartSigningKeyRing? keyRing = null)
    {
        _config = config;
        _registry = registry;
        _keyRing = keyRing;
    }

    /// <summary>GET /fhir/r4/.well-known/smart-configuration</summary>
    [HttpGet(".well-known/smart-configuration")]
    [Produces("application/json")]
    [ProducesResponseType(200)]
    public IActionResult GetSmartConfiguration()
    {
        var endpoints = ResolveAuthorizationServer();
        var authBase = endpoints.Issuer;

        var fhirBase = _config["Fhir:ServerBaseUrl"]
            ?? $"{Request.Scheme}://{Request.Host}/fhir/r4";

        var config = new
        {
            // ── Core endpoints ────────────────────────────────────────────────
            issuer = authBase,
            jwks_uri = endpoints.JwksUri,
            authorization_endpoint = endpoints.AuthorizationEndpoint,
            token_endpoint = endpoints.TokenEndpoint,
            token_endpoint_auth_methods_supported = new[]
            {
                "client_secret_basic",
                "client_secret_post",
                "private_key_jwt"
            },
            token_endpoint_auth_signing_alg_values_supported = new[] { "RS256" },
            introspection_endpoint = $"{authBase}/connect/introspect",
            introspection_endpoint_auth_methods_supported = new[]
            {
                "client_secret_basic",
                "client_secret_post"
            },
            end_session_endpoint = $"{authBase}/connect/logout",
            userinfo_endpoint = $"{authBase}/connect/userinfo",

            // ── SMART-specific fields ─────────────────────────────────────────
            grant_types_supported = new[] { "authorization_code", "client_credentials" },
            response_types_supported = new[] { "code" },
            code_challenge_methods_supported = new[] { "S256" },

            scopes_supported = new[]
            {
                "openid", "profile", "fhirUser",
                "launch", "launch/patient", "launch/encounter",
                "patient/*.read",
                "user/*.read",
                "system/*.read",
                "patient/Patient.read",
                "patient/Coverage.read",
                "patient/ExplanationOfBenefit.read",
                "patient/Encounter.read",
                "patient/Claim.read",
                "user/Patient.read",
                "user/Coverage.read",
                "user/ExplanationOfBenefit.read",
                "user/Encounter.read",
                "user/Claim.read",

                // Writes. The FHIR surface serves genuine writes (PAS
                // Claim/$submit, CDex $submit-attachment, DTR authoring) and a
                // read scope does not authorize any of them, so the write scopes
                // have to be discoverable. There is no patient-context write
                // scope: every write here is a provider/payer transaction.
                "user/*.write",
                "system/*.write",
                "user/Claim.write",
                "system/Claim.write",
                "user/Task.write",
                "system/Task.write",
                "user/Questionnaire.write",
                "system/Questionnaire.write",
                "user/QuestionnaireResponse.write",
                "system/QuestionnaireResponse.write"
            },

            // ── SMART capabilities advertised ─────────────────────────────────
            capabilities = new[]
            {
                "launch-ehr",
                "launch-standalone",
                "client-public",
                "client-confidential-symmetric",
                "permission-patient",
                "permission-user",
                "permission-system",
                "sso-openid-connect",
                "context-ehr-patient",
                "context-ehr-encounter",
                "context-standalone-patient"
            },

            // ── FHIR base ─────────────────────────────────────────────────────
            fhirVersion = "4.0.1",
            fhirBaseUrl = fhirBase
        };

        return Ok(config);
    }

    /// <summary>
    /// The authorization server a SMART client should actually be sent to.
    ///
    /// CHO is a resource server; it does not host an authorization flow in
    /// ExternalIssuer mode, so advertising one built from its own URL patterns
    /// would point every client at endpoints that do not exist. Real issuers
    /// disagree about paths — Okta serves /v1/authorize, Entra
    /// /oauth2/v2.0/authorize, OpenIddict /connect/authorize — which is exactly
    /// why the values come from the issuer's discovery document rather than
    /// from string concatenation here.
    ///
    /// The Demo path keeps the bundled smart-auth-service's OpenIddict layout,
    /// which CHO does host and therefore does know.
    /// </summary>
    private AuthorizationServerEndpoints ResolveAuthorizationServer()
    {
        var trusted = _registry?.Issuers.FirstOrDefault();

        if (_registry?.Mode == SmartTrustMode.ExternalIssuer && trusted != null)
        {
            var metadata = _keyRing?.MetadataFor(trusted.Issuer);
            return new AuthorizationServerEndpoints(
                Issuer: trusted.Issuer,
                // Null rather than a guess when discovery has not completed: a
                // client that reads a fabricated endpoint fails confusingly,
                // one that reads a missing field retries.
                JwksUri: metadata?.JwksUri,
                AuthorizationEndpoint: metadata?.AuthorizationEndpoint,
                TokenEndpoint: metadata?.TokenEndpoint);
        }

        var authBase = trusted?.Issuer
            ?? _config["SmartAuth:Issuer"]
            ?? throw new InvalidOperationException("SmartAuth:Issuer is not configured.");

        return new AuthorizationServerEndpoints(
            Issuer: authBase,
            JwksUri: $"{authBase}/.well-known/jwks",
            AuthorizationEndpoint: $"{authBase}/connect/authorize",
            TokenEndpoint: $"{authBase}/connect/token");
    }

    private sealed record AuthorizationServerEndpoints(
        string Issuer, string? JwksUri, string? AuthorizationEndpoint, string? TokenEndpoint);
}
