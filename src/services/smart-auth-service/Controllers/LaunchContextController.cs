using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using SmartAuthService.Models;
using SmartAuthService.Services;

namespace SmartAuthService.Controllers;

/// <summary>
/// EHR launch context registration endpoint.
///
/// Before an EHR redirects a provider to a SMART application, it registers
/// the patient/encounter context here and receives a single-use launch token
/// (TTL SmartAuth:LaunchContextTtlMinutes, default 5 minutes) to put in the
/// authorization URL as &amp;launch={token}.
///
/// The caller authenticates with a CHO token holding <c>members:read</c>
/// (it is opening a member's record). The launch belongs to that token's
/// tenant — there is no tenant in the body and an X-Tenant-ID header is at
/// most an echo of the token's (the shared tenant middleware refuses a
/// mismatch). The client must be registered to the same tenant, and at
/// authorization the launch is honoured only for a provider user of that
/// tenant using that client — and, when the body names a
/// <c>practitionerId</c>, only for the provider user bound to that provider.
/// A member's patient is never taken from a launch.
///
/// The registering caller is a CHO user or service, not a SMART provider
/// identity, so the practitioner cannot be read from its token; the EHR names
/// it. A launch without one can be used by any provider user of the tenant
/// using that client (who holds the single-use token).
/// </summary>
[ApiController]
[Route("launch")]
[RequirePermission("members:read")]
public class LaunchContextController : ControllerBase
{
    private readonly ILaunchContextStore _store;
    private readonly ISmartIdentityStore _identities;
    private readonly ICurrentActor _actor;
    private readonly IConfiguration _config;
    private readonly ILogger<LaunchContextController> _logger;

    public LaunchContextController(
        ILaunchContextStore store,
        ISmartIdentityStore identities,
        ICurrentActor actor,
        IConfiguration config,
        ILogger<LaunchContextController> logger)
    {
        _store = store;
        _identities = identities;
        _actor = actor;
        _config = config;
        _logger = logger;
    }

    /// <summary>POST /launch — register an EHR launch context for the caller's tenant.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RegisterLaunchResponse), 200)]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterLaunchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PatientId) && string.IsNullOrEmpty(request.EncounterId))
            return BadRequest(new { error = "At least one of patientId or encounterId is required." });
        if ((request.PatientId != null && !SmartIdentifiers.IsFhirId(request.PatientId))
            || (request.EncounterId != null && !SmartIdentifiers.IsFhirId(request.EncounterId))
            || (request.PractitionerId != null && !SmartIdentifiers.IsFhirId(request.PractitionerId)))
            return BadRequest(new { error = "patientId, encounterId and practitionerId must be FHIR ids." });

        var tenantId = _actor.TenantId;
        var client = await _identities.FindClientAsync(request.ClientId, ct);
        if (client == null || client.TenantId != tenantId || client.Kind != SmartClientKind.ProviderApp)
            return BadRequest(new { error = "clientId is not a provider app registered to this tenant." });

        var token = await _store.RegisterAsync(tenantId, _actor.UserId, request, ct);

        _logger.LogInformation(
            "EHR launch registered — tenant: {Tenant}, actor: {Actor}, client: {ClientId}, patient: {PatientId}, encounter: {EncounterId}, practitioner: {PractitionerId}",
            SmartAuthAudit.Clean(tenantId), SmartAuthAudit.Clean(_actor.UserId), SmartAuthAudit.Clean(request.ClientId),
            SmartAuthAudit.Clean(request.PatientId), SmartAuthAudit.Clean(request.EncounterId),
            SmartAuthAudit.Clean(request.PractitionerId));

        // Return the launch token and the ISS (FHIR base URL) the EHR needs
        return Ok(new
        {
            launch = token,
            iss = _config["SmartAuth:FhirBaseUrl"] ?? string.Empty
        });
    }
}
