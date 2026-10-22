using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Logging;
using EncounterService.Fhir;
using EncounterService.Models;
using EncounterService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace EncounterService.Controllers;

/// <summary>
/// FHIR R4 Encounter read and search. encounter-service owns the projection;
/// fhir-service parses FHIR search syntax and calls this endpoint with
/// resolved, structured filters, exactly as it does for member-service's
/// Patient and coverage-service's Coverage (ADR 018). This endpoint never
/// parses FHIR search syntax.
///
/// <para>
/// Tenancy: the tenant comes from the validated token (shared
/// TenantMiddleware via UseChoAuthentication) and every query filters on it,
/// so a caller cannot read another tenant's encounters whatever it asks for.
/// Reads need <c>encounters:read</c>, the service's default read permission.
/// </para>
///
/// <para>
/// The routes sit under the service's own <c>api/encounters</c> prefix rather
/// than the <c>api/v1</c> that member-service and coverage-service use; this
/// service has no v1 segment and inventing one here would leave two prefixes
/// in one service. fhir-service holds the path as a constant.
/// </para>
/// </summary>
[ApiController]
[Route("api/encounters")]
public sealed class FhirEncounterController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";

    private readonly IEncounterRepository _repository;
    private readonly IEncounterFhirSearchRepository _search;
    private readonly IEncounterFhirProjector _projector;
    private readonly ILogger<FhirEncounterController> _logger;

    public FhirEncounterController(
        IEncounterRepository repository,
        IEncounterFhirSearchRepository search,
        IEncounterFhirProjector projector,
        ILogger<FhirEncounterController> logger)
    {
        _repository = repository;
        _search = search;
        _projector = projector;
        _logger = logger;
    }

    private string TenantId => HttpContext.Items["TenantId"]?.ToString() ?? string.Empty;

    /// <summary>FHIR R4 Encounter projection of one encounter.</summary>
    [AuditRecordAccess("Encounter", "Fhir", RecordIdRouteValue = "id")]
    [HttpGet("{id}/fhir")]
    [Produces(FhirContentType, "application/json")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetFhirEncounter([FromRoute] string id)
    {
        var encounter = await _repository.GetByIdAsync(id);
        if (encounter is null) return NotFound();

        return new ContentResult
        {
            ContentType = FhirContentType,
            Content = _projector.Project(encounter).ToJsonString(),
            StatusCode = 200,
        };
    }

    /// <summary>
    /// FHIR R4 Encounter projections of the encounters matching structured
    /// filters. Results are ordered by service start, newest first, and the
    /// total counts every match so the caller can page.
    /// </summary>
    [AuditRecordAccess("Encounter", RecordAccessActions.Search, ResultIdProperty = "EncounterId", MemberQueryParameters = ["memberId"])]
    [HttpGet("fhir/Encounter")]
    [ProducesResponseType(typeof(EncounterFhirSearchResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SearchFhirEncounters(
        [FromQuery] string? encounterId = null,
        [FromQuery] string? memberId = null,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] string? status = null,
        [FromQuery] EncounterType? encounterType = null,
        [FromQuery] DateTime? lastUpdatedFrom = null,
        [FromQuery] DateTime? lastUpdatedTo = null,
        [FromQuery][Range(0, EncounterFhirSearchQuery.MaxOffset)] int offset = 0,
        [FromQuery][Range(0, EncounterFhirSearchQuery.MaxLimit)] int limit = 20)
    {
        // A status the projection never emits is a caller error, not an empty
        // result: fhir-service validates the code set before it gets here, so
        // reaching this means the two disagree and silence would hide it.
        if (status is not null && !FhirEncounterStatusCodes.IsEmitted(status.Trim().ToLowerInvariant()))
        {
            ModelState.AddModelError(nameof(status),
                $"status must be {FhirEncounterStatusCodes.Finished} or {FhirEncounterStatusCodes.EnteredInError}.");
            return ValidationProblem(ModelState);
        }

        var query = new EncounterFhirSearchQuery
        {
            EncounterId = encounterId,
            MemberId = memberId,
            ServiceDateFrom = serviceDateFrom,
            ServiceDateTo = serviceDateTo,
            Status = status,
            EncounterType = encounterType,
            LastUpdatedFrom = lastUpdatedFrom,
            LastUpdatedTo = lastUpdatedTo,
            Offset = offset,
            Limit = limit,
        };

        if (string.IsNullOrEmpty(TenantId))
        {
            _logger.LogError("FHIR Encounter search reached the controller without a tenant on the request.");
            return ValidationProblem();
        }

        var (encounters, total) = await _search.SearchAsync(TenantId, query, HttpContext.RequestAborted);
        var items = encounters
            .Select(e => new EncounterFhirSearchEntry(e.Id, e.MemberId, _projector.Project(e)))
            .ToList();

        return Ok(new EncounterFhirSearchResponse(total, items));
    }
}
