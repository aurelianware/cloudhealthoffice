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

    public SmartConfigurationController(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>GET /fhir/r4/.well-known/smart-configuration</summary>
    [HttpGet(".well-known/smart-configuration")]
    [Produces("application/json")]
    [ProducesResponseType(200)]
    public IActionResult GetSmartConfiguration()
    {
        var authBase = _config["SmartAuth:Issuer"]
            ?? throw new InvalidOperationException("SmartAuth:Issuer is not configured.");

        var fhirBase = _config["Fhir:ServerBaseUrl"]
            ?? $"{Request.Scheme}://{Request.Host}/fhir/r4";

        var config = new
        {
            // ── Core endpoints ────────────────────────────────────────────────
            issuer = authBase,
            jwks_uri = $"{authBase}/.well-known/jwks",
            authorization_endpoint = $"{authBase}/connect/authorize",
            token_endpoint = $"{authBase}/connect/token",
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
                "user/Claim.read"
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
}
