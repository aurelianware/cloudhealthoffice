using Microsoft.AspNetCore.Mvc;
using SmartAuthService.Models;
using SmartAuthService.Services;

namespace SmartAuthService.Controllers;

/// <summary>
/// EHR launch context registration endpoint.
///
/// Before an EHR redirects a provider to a SMART app, it calls POST /launch
/// to register the patient/encounter context.  The returned launch token is
/// included in the authorization URL as &amp;launch={token}.
///
/// The token is single-use and expires after SmartAuth:LaunchContextTtlMinutes
/// (default 5 minutes).
///
/// Security: in production this endpoint must be protected by mutual TLS or a
/// shared secret so only trusted EHR systems can register launch contexts.
/// Sprint 2: open for integration testing (restrict via network policy in k8s).
/// </summary>
[ApiController]
[Route("launch")]
public class LaunchContextController : ControllerBase
{
    private readonly ILaunchContextStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<LaunchContextController> _logger;

    public LaunchContextController(
        ILaunchContextStore store,
        IConfiguration config,
        ILogger<LaunchContextController> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// POST /launch — register an EHR launch context.
    /// Returns an opaque launch token to embed in the SMART authorization URL.
    /// </summary>
    /// <remarks>
    /// Example request:
    /// <code>
    /// POST /launch
    /// Content-Type: application/json
    ///
    /// {
    ///   "patientId":    "pat-001",
    ///   "encounterId":  "enc-003",
    ///   "clientId":     "cho-ehr-app"
    /// }
    /// </code>
    ///
    /// Example response:
    /// <code>{ "launch": "abc123..." }</code>
    ///
    /// Authorization URL the EHR then uses:
    /// <code>
    /// GET /connect/authorize
    ///   ?response_type=code
    ///   &amp;client_id=cho-ehr-app
    ///   &amp;redirect_uri=https://portal.cloudhealthoffice.com/smart/callback
    ///   &amp;scope=launch/patient launch/encounter openid user/*.read
    ///   &amp;launch=abc123...
    ///   &amp;iss=https://api.cloudhealthoffice.com/fhir/r4
    /// </code>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(RegisterLaunchResponse), 200)]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterLaunchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PatientId) && string.IsNullOrEmpty(request.EncounterId))
            return BadRequest(new { error = "At least one of patientId or encounterId is required." });

        var token = await _store.RegisterAsync(request, ct);

        _logger.LogInformation(
            "EHR launch registered — client: {ClientId}, patient: {PatientId}, encounter: {EncounterId}",
            SanitizeForLog(request.ClientId), SanitizeForLog(request.PatientId), SanitizeForLog(request.EncounterId));

        var fhirBase = _config["SmartAuth:FhirBaseUrl"] ?? string.Empty;

        // Return the launch token and the ISS (FHIR base URL) the EHR needs
        return Ok(new
        {
            launch = token,
            iss = fhirBase
        });
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
