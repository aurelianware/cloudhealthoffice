using FhirService.Services.Identity;
using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Dtr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci DTR (Documentation Templates &amp; Rules) controller.
/// FHIR R4 Questionnaire/QuestionnaireResponse CRUD and $questionnaire-package operation.
/// </summary>
[Route("fhir/r4")]
public class DtrController : FhirControllerBase
{
    private readonly IDtrService _dtrService;
    private readonly FhirBundleBuilder _bundleBuilder;
    private readonly ILogger<DtrController> _logger;

    public DtrController(
        IDtrService dtrService,
        FhirBundleBuilder bundleBuilder,
        ILogger<DtrController> logger)
    {
        _dtrService = dtrService;
        _bundleBuilder = bundleBuilder;
        _logger = logger;
    }

    // ── Questionnaire endpoints ──────────────────────────────────────────────

    /// <summary>GET /fhir/r4/Questionnaire/{id}</summary>
    [HttpGet("Questionnaire/{id}")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [ProducesResponseType(typeof(Questionnaire), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> GetQuestionnaire(string id, CancellationToken ct)
    {
        var q = await _dtrService.GetQuestionnaireAsync(id, TenantId, ct);
        return q is null ? FhirNotFound("Questionnaire", id) : Ok(q);
    }

    /// <summary>GET /fhir/r4/Questionnaire — search</summary>
    [HttpGet("Questionnaire")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> SearchQuestionnaires(
        [FromQuery] QuestionnaireSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _dtrService.SearchQuestionnairesAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Questionnaire", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }

    /// <summary>
    /// D-14: <c>"default"</c> is the store key of the read-only library every
    /// tenant's search includes. A tenant whose id is "default" writing there
    /// would publish its forms to every tenant, so that id is reserved for
    /// questionnaire writes.
    /// </summary>
    private bool IsReservedLibraryTenant
        => string.Equals(TenantId, DtrService.DefaultLibraryTenant, StringComparison.Ordinal);

    private const string ReservedLibraryMessage =
        "Tenant id 'default' is reserved for the shared, read-only questionnaire library.";

    /// <summary>POST /fhir/r4/Questionnaire — create</summary>
    [HttpPost("Questionnaire")]
    // CHO administrators only (D-15): a payer's questionnaires are its
    // documentation rules. SMART callers read them and answer them
    // (QuestionnaireResponse); they never author them.
    [FhirAccess(smart: false, cho: "settings:manage")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Questionnaire), 201)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> CreateQuestionnaire(
        [FromBody] Questionnaire questionnaire, CancellationToken ct)
    {
        if (IsReservedLibraryTenant) return FhirForbidden(ReservedLibraryMessage);

        // Validate required fields
        if (questionnaire.Status == null)
            return FhirBadRequest("Questionnaire.status is required");
        if (questionnaire.Item == null || questionnaire.Item.Count == 0)
            return FhirBadRequest("Questionnaire must contain at least one item");

        var validation = ValidateItems(questionnaire.Item);
        if (validation != null) return validation;

        var created = await _dtrService.CreateQuestionnaireAsync(questionnaire, TenantId, ct);

        _logger.LogInformation("Created Questionnaire {Id} for tenant {TenantId}",
            LogSanitizer.Sanitize(created.Id), LogSanitizer.Sanitize(TenantId));

        SetLocationHeader("Questionnaire", created.Id);
        return StatusCode(201, created);
    }

    /// <summary>PUT /fhir/r4/Questionnaire/{id} — update</summary>
    [HttpPut("Questionnaire/{id}")]
    // CHO administrators only (D-15): a payer's questionnaires are its
    // documentation rules. SMART callers read them and answer them
    // (QuestionnaireResponse); they never author them.
    [FhirAccess(smart: false, cho: "settings:manage")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Questionnaire), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> UpdateQuestionnaire(
        string id, [FromBody] Questionnaire questionnaire, CancellationToken ct)
    {
        if (IsReservedLibraryTenant) return FhirForbidden(ReservedLibraryMessage);

        if (questionnaire.Status == null)
            return FhirBadRequest("Questionnaire.status is required");
        if (questionnaire.Item == null || questionnaire.Item.Count == 0)
            return FhirBadRequest("Questionnaire must contain at least one item");

        var validation = ValidateItems(questionnaire.Item);
        if (validation != null) return validation;

        var updated = await _dtrService.UpdateQuestionnaireAsync(id, questionnaire, TenantId, ct);
        if (updated == null)
            return FhirNotFound("Questionnaire", id);

        _logger.LogInformation("Updated Questionnaire {Id} for tenant {TenantId}",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(TenantId));

        return Ok(updated);
    }

    // ── QuestionnaireResponse endpoints ──────────────────────────────────────

    /// <summary>GET /fhir/r4/QuestionnaireResponse/{id}</summary>
    [HttpGet("QuestionnaireResponse/{id}")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [ProducesResponseType(typeof(QuestionnaireResponse), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> GetQuestionnaireResponse(string id, CancellationToken ct)
    {
        var qr = await _dtrService.GetResponseAsync(id, TenantId, ct);
        // QuestionnaireResponses hold clinical answers: a patient-bound token
        // reads only its own patient's, a user/system token only the member
        // Provider Access authorized it for; anyone else's is "not found".
        return qr is null || IsOutsidePatientContext(qr.Subject?.Reference)
            ? FhirNotFound("QuestionnaireResponse", id)
            : Ok(qr);
    }

    /// <summary>GET /fhir/r4/QuestionnaireResponse — search</summary>
    [HttpGet("QuestionnaireResponse")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> SearchQuestionnaireResponses(
        [FromQuery] QuestionnaireResponseSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        // Confined to one member: a patient-bound token's own patient (the
        // middleware has already refused a patient/subject naming someone else),
        // or the member Provider Access authorized a user/system token for (a
        // search naming no member never gets here).
        if (AuthorizedMemberId is { } bound) search.Patient = bound;

        var (items, total) = await _dtrService.SearchResponsesAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "QuestionnaireResponse", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }

    /// <summary>POST /fhir/r4/QuestionnaireResponse — submit completed response</summary>
    [HttpPost("QuestionnaireResponse")]
    [FhirAccess(smart: true, cho: "authorizations:write")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(QuestionnaireResponse), 201)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> SubmitQuestionnaireResponse(
        [FromBody] QuestionnaireResponse response, CancellationToken ct)
    {
        // Validate status — only completed or amended submissions are accepted
        if (response.Status is not (QuestionnaireResponse.QuestionnaireResponseStatus.Completed
            or QuestionnaireResponse.QuestionnaireResponseStatus.Amended))
            return FhirBadRequest(
                "QuestionnaireResponse.status must be 'completed' or 'amended'");

        // Validate questionnaire reference
        if (string.IsNullOrEmpty(response.Questionnaire))
            return FhirBadRequest("QuestionnaireResponse.questionnaire reference is required");

        // Resolved as a canonical too: a DTR client answers the url|version the
        // package named, not a local id.
        if (!await _dtrService.QuestionnaireExistsAsync(response.Questionnaire, TenantId, FhirBaseUrl, ct))
            return FhirBadRequest(
                $"Referenced Questionnaire '{response.Questionnaire}' does not exist");

        // Validate subject
        if (response.Subject == null || string.IsNullOrEmpty(response.Subject.Reference))
            return FhirBadRequest("QuestionnaireResponse.subject (patient reference) is required");

        if (IsOutsidePatientContext(response.Subject.Reference))
        {
            return StatusCode(403, new OperationOutcome
            {
                Issue =
                [
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Error,
                        Code = OperationOutcome.IssueType.Forbidden,
                        Diagnostics = "A patient-context token cannot submit a QuestionnaireResponse for another patient.",
                    },
                ],
            });
        }

        var submitted = await _dtrService.SubmitResponseAsync(response, TenantId, ct);

        _logger.LogInformation("Submitted QuestionnaireResponse {Id} for tenant {TenantId}",
            LogSanitizer.Sanitize(submitted.Id), LogSanitizer.Sanitize(TenantId));

        SetLocationHeader("QuestionnaireResponse", submitted.Id);
        return StatusCode(201, submitted);
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    /// <summary>
    /// POST /fhir/r4/Questionnaire/$questionnaire-package — DTR 2.2.0.
    ///
    /// Takes the member's Coverage plus the order(s), questionnaire canonical(s)
    /// or CRD context the documentation is for, and answers with a Parameters
    /// holding one package Bundle per questionnaire: the Questionnaire, a draft
    /// QuestionnaireResponse, and the CQL Libraries and ValueSets the DTR client
    /// needs to prepopulate it. Requested questionnaires that do not exist are
    /// warnings in the outcome (oper-9), not failures; unusable source data is
    /// a 4xx with an OperationOutcome (spec-130).
    /// </summary>
    [HttpPost("Questionnaire/$questionnaire-package")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Parameters), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 422)]
    public async Task<IActionResult> QuestionnairePackage(
        [FromBody] Parameters parameters, CancellationToken ct)
    {
        var (request, error) = QuestionnairePackageRequest.Parse(parameters);
        if (request is null)
            return FhirBadRequest(error!);

        var result = await _dtrService.GetQuestionnairePackageAsync(request, TenantId, FhirBaseUrl, ct);
        return result.Package is { } package
            ? Ok(package)
            : StatusCode(result.Status, result.Rejection);
    }

    // ── $next-question ───────────────────────────────────────────────────────

    /// <summary>
    /// POST /fhir/r4/Questionnaire/$next-question — SDC adaptive forms, as DTR
    /// 2.2.0 profiles them. The body is the client's adaptive
    /// QuestionnaireResponse (or a Parameters carrying it as
    /// <c>questionnaire-response</c>); the answer is the same response with the
    /// next question added to its contained Questionnaire, or marked completed.
    /// A response the payer finds invalid is a 400 with an OperationOutcome
    /// (spec-147). Nothing the client sends is stored (sec-10).
    /// </summary>
    [HttpPost("Questionnaire/$next-question")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(QuestionnaireResponse), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 422)]
    public IActionResult NextQuestion([FromBody] Resource body)
    {
        var response = body switch
        {
            QuestionnaireResponse qr => qr,
            Parameters p => p.Parameter.FirstOrDefault(x => x.Name == "questionnaire-response")?.Resource
                as QuestionnaireResponse,
            _ => null,
        };
        if (response is null)
            return FhirBadRequest("$next-question takes a QuestionnaireResponse, or a Parameters with a " +
                                  "'questionnaire-response' QuestionnaireResponse.");

        var result = new AdaptiveQuestionnaireEngine(DtrContentLibrary.Default).Next(response, DateTimeOffset.UtcNow);

        _logger.LogInformation("$next-question for tenant {TenantId}: HTTP {Status}, status {QrStatus}",
            LogSanitizer.Sanitize(TenantId), result.Status, result.Response?.Status?.ToString() ?? "-");

        return result.Response is { } next ? Ok(next) : StatusCode(result.Status, result.Rejection);
    }

    // ── $log-questionnaire-errors ───────────────────────────────────────────

    /// <summary>
    /// POST /fhir/r4/Questionnaire/$log-questionnaire-errors — DTR 2.2.0
    /// (oper-1). A DTR client reports a problem it hit with one of the payer's
    /// questionnaires: the questionnaire canonical and an OperationOutcome. CHO
    /// logs the canonical and the issue severities and codes so the plan can
    /// fix its content. Issue text is not logged: a client may quote form data
    /// in it, and the log is not a place for PHI.
    /// </summary>
    [HttpPost("Questionnaire/$log-questionnaire-errors")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(OperationOutcome), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public IActionResult LogQuestionnaireErrors([FromBody] Parameters parameters)
    {
        var canonical = parameters.Parameter.FirstOrDefault(p => p.Name == "questionnaire")?.Value switch
        {
            Canonical c => c.Value,
            FhirUri u => u.Value,
            FhirString str => str.Value,
            _ => null,
        };
        var outcome = parameters.Parameter.FirstOrDefault(p => p.Name == "operationOutcome")?.Resource as OperationOutcome;
        if (string.IsNullOrWhiteSpace(canonical) || outcome is null)
            return FhirBadRequest("$log-questionnaire-errors takes a 'questionnaire' canonical and an " +
                                  "'operationOutcome' describing the errors.");

        var issues = string.Join(", ", outcome.Issue.Select(i => $"{i.Severity}/{i.Code}"));
        _logger.LogWarning(
            "DTR client reported questionnaire errors for tenant {TenantId}, questionnaire {Questionnaire}: {Issues}",
            LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(canonical, 512), LogSanitizer.Sanitize(issues, 512));

        return Ok(new OperationOutcome
        {
            Issue =
            {
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Code = OperationOutcome.IssueType.Informational,
                    Diagnostics = $"Recorded {outcome.Issue.Count} issue(s) for {canonical}.",
                },
            },
        });
    }

    // ── ValueSet/$expand ─────────────────────────────────────────────────────

    /// <summary>
    /// GET or POST /fhir/r4/ValueSet/$expand — the DTR 2.2.0 profile of
    /// <c>$expand</c> (<c>url</c> required, optional <c>filter</c>): expands a
    /// ValueSet a DTR questionnaire uses, for a client that received it
    /// unexpanded in a package. Only explicit concept lists are expandable;
    /// CHO runs no terminology server.
    /// </summary>
    [HttpGet("ValueSet/$expand")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(ValueSet), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public IActionResult ExpandValueSet([FromQuery] string? url, [FromQuery] string? filter)
        => Expand(url, filter);

    [HttpPost("ValueSet/$expand")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(ValueSet), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public IActionResult ExpandValueSetPost([FromBody] Parameters parameters)
    {
        string? Text(string name) => parameters.Parameter.FirstOrDefault(p => p.Name == name)?.Value switch
        {
            FhirUri u => u.Value,
            FhirString str => str.Value,
            Canonical c => c.Value,
            _ => null,
        };
        return Expand(Text("url"), Text("filter"));
    }

    private IActionResult Expand(string? url, string? filter)
    {
        if (string.IsNullOrWhiteSpace(url))
            return FhirBadRequest("ValueSet/$expand requires the 'url' of the ValueSet to expand.");

        var valueSet = DtrContentLibrary.Default.ResolveValueSet(url);
        if (valueSet is null)
        {
            return StatusCode(404, new OperationOutcome
            {
                Issue =
                {
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Error,
                        Code = OperationOutcome.IssueType.NotFound,
                        Diagnostics = $"This server holds no ValueSet {LogSanitizer.Sanitize(url, 512)}.",
                    },
                },
            });
        }

        var issues = new List<OperationOutcome.IssueComponent>();
        if (!QuestionnairePackageBuilder.TryExpand(valueSet, DateTimeOffset.UtcNow, filter, issues))
            return StatusCode(422, new OperationOutcome { Issue = issues });
        return Ok(valueSet);
    }

    // ── Validation helpers ───────────────────────────────────────────────────

    private IActionResult? ValidateItems(List<Questionnaire.ItemComponent> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.LinkId))
                return FhirBadRequest("Each Questionnaire.item must have a linkId");
            if (item.Type == null)
                return FhirBadRequest($"Questionnaire.item '{item.LinkId}' must have a type");
            if (item.Item is { Count: > 0 })
            {
                var nested = ValidateItems(item.Item);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeIdPattern = new(
        @"^[A-Za-z0-9\-\.]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void SetLocationHeader(string resourceType, string id)
    {
        if (!SafeIdPattern.IsMatch(id)) return;
        Response.Headers["Location"] = $"{FhirBaseUrl}/{resourceType}/{id}";
    }
}
