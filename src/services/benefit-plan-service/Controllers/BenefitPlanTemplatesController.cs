using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Templates;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// State program benefit plan templates (Texas STAR, CHIP, ... first). Reads
/// use benefits:read; creating a Draft from a template uses the service's
/// default write permission (settings:manage). See
/// docs/integration/STATE-PROGRAM-BENEFIT-TEMPLATES.md.
/// </summary>
[ApiController]
[Route("api/v1/plans/templates")]
public sealed class BenefitPlanTemplatesController : ControllerBase
{
    private readonly IBenefitPlanTemplateCatalog _catalog;
    private readonly IBenefitPlanService _plans;
    private readonly ICurrentActor _actor;

    public BenefitPlanTemplatesController(IBenefitPlanTemplateCatalog catalog, IBenefitPlanService plans, ICurrentActor actor)
    {
        _catalog = catalog;
        _plans = plans;
        _actor = actor;
    }

    /// <summary>Templates, optionally for one state and program.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TemplateSummary>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<TemplateSummary>> List([FromQuery] string? state = null, [FromQuery] string? program = null) =>
        Ok(_catalog.List(state, program).Select(TemplateSummary.From));

    /// <summary>One template, with the plan it would create.</summary>
    [HttpGet("{templateId}")]
    [ProducesResponseType(typeof(TemplateDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TemplateDetail> Get(string templateId)
    {
        var t = _catalog.Get(templateId);
        if (t is null) return NotFound(new { message = $"Template '{templateId}' not found." });
        var preview = t.ToBenefitPlan($"preview-{t.TemplateId}", null, "(payer)", DateTime.UtcNow.Date, null, null);
        return Ok(new TemplateDetail(t, preview));
    }

    /// <summary>
    /// Creates a Draft plan from the template. Nothing is published; the plan's
    /// benefits team checks every value marked verify against its contract,
    /// adds its value-added services and prior-auth rules, and publishes.
    /// </summary>
    [HttpPost("{templateId}/drafts")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BenefitPlan>> CreateDraft(string templateId, [FromBody] CreateFromTemplateRequest request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var t = _catalog.Get(templateId);
        if (t is null) return NotFound(new { message = $"Template '{templateId}' not found." });

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });

        var existing = await _plans.ListVersionsAsync(request.PlanId, tenantId, 1, null);
        if (existing.Items.Count > 0)
            return Conflict(new { message = $"Plan '{request.PlanId}' already exists; amend it instead.", versionId = existing.Items[0].VersionId });

        var plan = t.ToBenefitPlan(request.PlanId, request.PlanName, request.Payer, request.EffectiveDate, request.TerminationDate, request.ProgramId);
        try
        {
            var draft = await _plans.CreateDraftAsync(plan, tenantId, _actor.UserId);
            return Created($"/api/v1/plans/{Uri.EscapeDataString(draft.PlanId)}/versions/{Uri.EscapeDataString(draft.VersionId)}", draft);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

public sealed class CreateFromTemplateRequest
{
    [Required, JsonPropertyName("planId")] public string PlanId { get; set; } = string.Empty;
    [JsonPropertyName("planName")] public string? PlanName { get; set; }
    [Required, JsonPropertyName("payer")] public string Payer { get; set; } = string.Empty;
    [Required, JsonPropertyName("effectiveDate")] public DateTime EffectiveDate { get; set; }
    [JsonPropertyName("terminationDate")] public DateTime? TerminationDate { get; set; }

    /// <summary>tenant-service program id (for example the tenant's "TX CHIP" program).</summary>
    [JsonPropertyName("programId")] public string? ProgramId { get; set; }
}

public sealed record TemplateSummary(
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("program")] string Program,
    [property: JsonPropertyName("planName")] string PlanName,
    [property: JsonPropertyName("population")] string Population,
    [property: JsonPropertyName("verifyCount")] int VerifyCount,
    [property: JsonPropertyName("notModeledCount")] int NotModeledCount)
{
    public static TemplateSummary From(BenefitPlanTemplate t) =>
        new(t.TemplateId, t.State, t.Program, t.PlanName, t.Population, t.VerifyCount, t.NotModeled.Count);
}

public sealed record TemplateDetail(
    [property: JsonPropertyName("template")] BenefitPlanTemplate Template,
    [property: JsonPropertyName("preview")] BenefitPlan Preview);
