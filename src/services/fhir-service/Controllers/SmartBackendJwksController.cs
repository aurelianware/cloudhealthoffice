using FhirService.Services.SmartBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Publishes the public keys CHO signs its SMART Backend Services client
/// assertions with (G-42 outbound), so another payer can register this URL as
/// CHO's <c>jwks_uri</c> and pick up a rotated key on its own. Public parts
/// only; 404 when CHO holds no signing key.
/// </summary>
[Route("fhir/r4/.well-known")]
[AllowAnonymous]
[ApiController]
public sealed class SmartBackendJwksController : ControllerBase
{
    private readonly SmartBackendKeyRing _keys;

    public SmartBackendJwksController(SmartBackendKeyRing keys) => _keys = keys;

    /// <summary>GET /fhir/r4/.well-known/smart-backend-jwks.json</summary>
    [HttpGet("smart-backend-jwks.json")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public IActionResult Get()
    {
        var jwks = _keys.PublicJwks();
        if (jwks is null) return NotFound();

        // Short enough that a peer sees a standby key well before it signs.
        Response.Headers.CacheControl = "public, max-age=300";
        return Content(jwks.ToJsonString(), "application/json");
    }
}
