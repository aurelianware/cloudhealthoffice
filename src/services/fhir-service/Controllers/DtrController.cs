using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci DTR (Documentation Templates &amp; Rules) controller.
/// FHIR R4 Questionnaire/QuestionnaireResponse CRUD and $questionnaire-package operation.
/// </summary>
[Route("fhir/r4")]
[Authorize]
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
    [ProducesResponseType(typeof(Questionnaire), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> GetQuestionnaire(string id, CancellationToken ct)
    {
        var q = await _dtrService.GetQuestionnaireAsync(id, TenantId, ct);
        return q is null ? FhirNotFound("Questionnaire", id) : Ok(q);
    }

    /// <summary>GET /fhir/r4/Questionnaire — search</summary>
    [HttpGet("Questionnaire")]
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

    /// <summary>POST /fhir/r4/Questionnaire — create</summary>
    [HttpPost("Questionnaire")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Questionnaire), 201)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> CreateQuestionnaire(
        [FromBody] Questionnaire questionnaire, CancellationToken ct)
    {
        // Validate required fields
        if (questionnaire.Status == null)
            return FhirBadRequest("Questionnaire.status is required");
        if (questionnaire.Item == null || questionnaire.Item.Count == 0)
            return FhirBadRequest("Questionnaire must contain at least one item");

        var validation = ValidateItems(questionnaire.Item);
        if (validation != null) return validation;

        var created = await _dtrService.CreateQuestionnaireAsync(questionnaire, TenantId, ct);

        _logger.LogInformation("Created Questionnaire {Id} for tenant {TenantId}",
            SanitizeForLog(created.Id), SanitizeForLog(TenantId));

        SetLocationHeader("Questionnaire", created.Id);
        return StatusCode(201, created);
    }

    /// <summary>PUT /fhir/r4/Questionnaire/{id} — update</summary>
    [HttpPut("Questionnaire/{id}")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Questionnaire), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> UpdateQuestionnaire(
        string id, [FromBody] Questionnaire questionnaire, CancellationToken ct)
    {
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
            SanitizeForLog(id), SanitizeForLog(TenantId));

        return Ok(updated);
    }

    // ── QuestionnaireResponse endpoints ──────────────────────────────────────

    /// <summary>GET /fhir/r4/QuestionnaireResponse/{id}</summary>
    [HttpGet("QuestionnaireResponse/{id}")]
    [ProducesResponseType(typeof(QuestionnaireResponse), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> GetQuestionnaireResponse(string id, CancellationToken ct)
    {
        var qr = await _dtrService.GetResponseAsync(id, TenantId, ct);
        return qr is null ? FhirNotFound("QuestionnaireResponse", id) : Ok(qr);
    }

    /// <summary>GET /fhir/r4/QuestionnaireResponse — search</summary>
    [HttpGet("QuestionnaireResponse")]
    [ProducesResponseType(typeof(Bundle), 200)]
    public async Task<IActionResult> SearchQuestionnaireResponses(
        [FromQuery] QuestionnaireResponseSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        var (items, total) = await _dtrService.SearchResponsesAsync(search, TenantId, ct);

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "QuestionnaireResponse", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }

    /// <summary>POST /fhir/r4/QuestionnaireResponse — submit completed response</summary>
    [HttpPost("QuestionnaireResponse")]
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

        if (!_dtrService.QuestionnaireExists(response.Questionnaire, TenantId))
            return FhirBadRequest(
                $"Referenced Questionnaire '{response.Questionnaire}' does not exist");

        // Validate subject
        if (response.Subject == null || string.IsNullOrEmpty(response.Subject.Reference))
            return FhirBadRequest("QuestionnaireResponse.subject (patient reference) is required");

        var submitted = await _dtrService.SubmitResponseAsync(response, TenantId, ct);

        _logger.LogInformation("Submitted QuestionnaireResponse {Id} for tenant {TenantId}",
            SanitizeForLog(submitted.Id), SanitizeForLog(TenantId));

        SetLocationHeader("QuestionnaireResponse", submitted.Id);
        return StatusCode(201, submitted);
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    /// <summary>POST /fhir/r4/Questionnaire/$questionnaire-package</summary>
    [HttpPost("Questionnaire/$questionnaire-package")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(Bundle), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> QuestionnairePackage(
        [FromBody] Parameters parameters, CancellationToken ct)
    {
        // Extract questionnaire ID from Parameters resource
        var questionnaireParam = parameters.Parameter
            .FirstOrDefault(p => p.Name == "questionnaire");
        var questionnaireId = (questionnaireParam?.Value as FhirUri)?.Value
            ?? (questionnaireParam?.Value as FhirString)?.Value;

        if (string.IsNullOrEmpty(questionnaireId))
            return FhirBadRequest("Parameter 'questionnaire' (URI or string) is required");

        // Strip "Questionnaire/" prefix if present
        if (questionnaireId.StartsWith("Questionnaire/", StringComparison.Ordinal))
            questionnaireId = questionnaireId["Questionnaire/".Length..];

        var patientParam = parameters.Parameter
            .FirstOrDefault(p => p.Name == "patient");
        var patientId = (patientParam?.Value as FhirString)?.Value;

        var bundle = await _dtrService.GetQuestionnairePackageAsync(
            questionnaireId, patientId, TenantId, ct);

        if (bundle == null)
            return FhirNotFound("Questionnaire", questionnaireId);

        return Ok(bundle);
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
