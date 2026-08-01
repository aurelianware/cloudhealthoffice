using Microsoft.AspNetCore.Mvc;
using BenefitPlanService.Adapters;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using MongoDB.Driver;

namespace BenefitPlanService.Controllers;

// TODO(deprecate-plans-route): Consolidate with BenefitPlanMemberViewController
// under the hyphenated "api/v1/benefit-plans" root. Tracked as a follow-up
// issue — the two parallel routes are acceptable short-term but not long-term.
[ApiController]
[Route("api/v1/plans")]
public class BenefitPlansController : ControllerBase
{
    private readonly IBenefitPlanService _service;
    private readonly BenefitPlanAdapterFactory _adapterFactory;
    private readonly ILogger<BenefitPlansController> _logger;

    public BenefitPlansController(
        IBenefitPlanService service,
        BenefitPlanAdapterFactory adapterFactory,
        ILogger<BenefitPlansController> logger)
    {
        _service = service;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get current tenant ID from request context
    /// </summary>
    private string TenantId => HttpContext.GetTenantId() ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// Get all benefit plans with optional filtering
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<BenefitPlan>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BenefitPlan>>> GetPlans(
        [FromQuery] string? payer = null,
        [FromQuery] string? planType = null,
        [FromQuery] bool? activeOnly = true)
    {
        _logger.LogInformation("Getting benefit plans for tenant {TenantId}: payer={Payer}, planType={PlanType}, activeOnly={ActiveOnly}", 
            SanitizeForLog(TenantId), SanitizeForLog(payer), SanitizeForLog(planType), activeOnly);
        
        var plans = await _service.GetPlansAsync(TenantId, payer, planType, activeOnly ?? true);
        return Ok(plans);
    }

    /// <summary>
    /// Get a specific benefit plan by ID. Reads route through the
    /// tenant-resolved <see cref="IBenefitPlanAdapter"/>; for current
    /// tenants the factory always returns the CHO adapter, preserving
    /// existing behavior.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BenefitPlan>> GetPlan(string id)
    {
        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetPlanAsync(new BenefitPlanAdapterRequest
        {
            TenantId = TenantId,
            PlanId = id,
            PlatformSettings = settings,
        });

        if (response.Plan == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return Ok(response.Plan.ToBenefitPlan());
    }

    /// <summary>
    /// Create a new benefit plan
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BenefitPlan>> CreatePlan([FromBody] BenefitPlan plan)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            PlanDocumentValidation.ValidateDocuments(plan.Documents);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { field = ex.ParamName, message = ex.Message });
        }

        try
        {
            var created = await _service.CreatePlanAsync(plan, TenantId);
            return CreatedAtAction(nameof(GetPlan), new { id = created.Id }, created);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
    }

    /// <summary>
    /// Update an existing benefit plan. Returns 409 Conflict when the
    /// target version is Published or Superseded — clients must create
    /// an amendment via <c>POST /api/v1/plans/{planId}/amend</c> instead.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BenefitPlan>> UpdatePlan(string id, [FromBody] BenefitPlan plan)
    {
        if (id != plan.Id)
        {
            return BadRequest(new { message = "ID mismatch" });
        }

        try
        {
            PlanDocumentValidation.ValidateDocuments(plan.Documents);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { field = ex.ParamName, message = ex.Message });
        }

        try
        {
            var updated = await _service.UpdatePlanAsync(plan, TenantId);
            if (updated == null)
            {
                return NotFound(new { message = $"Benefit plan '{id}' not found" });
            }
            return Ok(updated);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new
            {
                message = ex.Message,
                planId = ex.PlanId,
                versionId = ex.VersionId,
                versionState = ex.CurrentState.ToString()
            });
        }
    }

    /// <summary>
    /// Delete a benefit plan. Terminates its current Published version
    /// (Superseded, no successor) rather than editing it in place.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeletePlan(string id)
    {
        try
        {
            var deleted = await _service.DeletePlanAsync(id, TenantId, ResolveActorId());
            if (!deleted)
            {
                return NotFound(new { message = $"Benefit plan '{id}' not found" });
            }

            return NoContent();
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Get benefits for a specific plan
    /// </summary>
    [HttpGet("{id}/benefits")]
    [ProducesResponseType(typeof(IEnumerable<Benefit>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Benefit>>> GetPlanBenefits(string id)
    {
        var plan = await _service.GetPlanAsync(id, TenantId);
        if (plan == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return Ok(plan.Benefits);
    }

    /// <summary>
    /// Add a benefit to a plan. Creates an amendment (new Draft), adds the
    /// benefit, and publishes it immediately -- benefits are identity
    /// content (5.1), so this cannot edit the Published row in place.
    /// </summary>
    [HttpPost("{id}/benefits")]
    [ProducesResponseType(typeof(Benefit), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Benefit>> AddBenefit(string id, [FromBody] Benefit benefit)
    {
        try
        {
            var added = await _service.AddBenefitAsync(id, TenantId, ResolveActorId(), benefit);
            if (added == null)
            {
                return NotFound(new { message = $"Benefit plan '{id}' not found" });
            }

            return CreatedAtAction(nameof(GetPlanBenefits), new { id }, added);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Replace a benefit rule. Creates and publishes a successor plan version
    /// so the current Published version remains immutable.
    /// </summary>
    [HttpPut("{id}/benefits/{benefitId}")]
    [ProducesResponseType(typeof(Benefit), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Benefit>> UpdateBenefit(
        string id,
        string benefitId,
        [FromBody] Benefit benefit)
    {
        try
        {
            var updated = await _service.UpdateBenefitAsync(
                id, benefitId, TenantId, ResolveActorId(), benefit);
            if (updated == null)
            {
                return NotFound(new { message = $"Benefit '{benefitId}' was not found on plan '{id}'" });
            }

            return Ok(updated);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Replace the plan's complete network-tier set. Creates and publishes a
    /// successor version because network tiers are plan identity content.
    /// An empty collection removes every tier.
    /// </summary>
    [HttpPut("{id}/network-tiers")]
    [ProducesResponseType(typeof(IReadOnlyList<NetworkTier>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<NetworkTier>>> ReplaceNetworkTiers(
        string id,
        [FromBody] List<NetworkTier> networkTiers)
    {
        try
        {
            var updated = await _service.ReplaceNetworkTiersAsync(
                id, TenantId, ResolveActorId(), networkTiers);
            if (updated == null)
                return NotFound(new { message = $"Benefit plan '{id}' not found" });

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { field = ex.ParamName, message = ex.Message });
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Get deductible and out-of-pocket accumulation data for a subscriber.
    /// Used by eligibility-service to populate the 271 response with
    /// deductible-met / OOP-met progress.
    /// </summary>
    [HttpGet("{id}/accumulation/{subscriberId}")]
    [ProducesResponseType(typeof(AccumulationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccumulationResponse>> GetAccumulation(string id, string subscriberId)
    {
        var plan = await _service.GetPlanAsync(id, TenantId);
        if (plan == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        var cs = plan.CostSharing;

        // Query the Accumulators collection (shared with the BenefitEngine) for this member's balances
        var db = HttpContext.RequestServices.GetRequiredService<IMongoDatabase>();
        var collection = db.GetCollection<AccumulatorDoc>("Accumulators");
        var filter = Builders<AccumulatorDoc>.Filter.And(
            Builders<AccumulatorDoc>.Filter.Eq(a => a.TenantId, TenantId),
            Builders<AccumulatorDoc>.Filter.Eq(a => a.OwnerId, subscriberId),
            Builders<AccumulatorDoc>.Filter.Eq(a => a.BenefitPlanId, id));
        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        decimal BalanceOf(string type) =>
            doc?.Balances?.FirstOrDefault(b => b.Type == type)?.AccumulatedAmount ?? 0m;

        var individualDeductibleMet = BalanceOf("IndividualDeductible");
        var familyDeductibleMet = BalanceOf("FamilyDeductible");
        var individualOopMet = BalanceOf("IndividualOOP");
        var familyOopMet = BalanceOf("FamilyOOP");

        var response = new AccumulationResponse
        {
            Deductible = new AccumulationDeductibleInfo
            {
                IndividualDeductible = cs.IndividualDeductible,
                IndividualDeductibleMet = individualDeductibleMet,
                IndividualDeductibleRemaining = Math.Max(0, cs.IndividualDeductible - individualDeductibleMet),
                FamilyDeductible = cs.FamilyDeductible,
                FamilyDeductibleMet = familyDeductibleMet,
                FamilyDeductibleRemaining = Math.Max(0, cs.FamilyDeductible - familyDeductibleMet),
                TimePeriod = "Calendar Year"
            },
            OutOfPocket = new AccumulationOutOfPocketInfo
            {
                IndividualOOPMax = cs.IndividualOutOfPocketMax,
                IndividualOOPMet = individualOopMet,
                IndividualOOPRemaining = Math.Max(0, cs.IndividualOutOfPocketMax - individualOopMet),
                FamilyOOPMax = cs.FamilyOutOfPocketMax,
                FamilyOOPMet = familyOopMet,
                FamilyOOPRemaining = Math.Max(0, cs.FamilyOutOfPocketMax - familyOopMet),
                TimePeriod = "Calendar Year"
            }
        };

        return Ok(response);
    }

    // -----------------------------------------------------------------
    // Version chain endpoints (5.1 — Plan Identity & Versioning)
    //
    // These endpoints address plans by their business <c>PlanId</c> (the
    // version-chain key shared across every version of the same plan), as
    // opposed to <c>GET /{id}</c> / <c>PUT /{id}</c> which address a single
    // immutable version document by its persistent row <c>Id</c>. The route
    // token is therefore <c>{planId}</c>, not <c>{id}</c>, to keep the two
    // identifiers distinct at the API surface.
    // -----------------------------------------------------------------

    /// <summary>
    /// Paginated, newest-first list of every version for a plan.
    /// </summary>
    [HttpGet("{planId}/versions")]
    [ProducesResponseType(typeof(PlanVersionPage), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlanVersionPage>> GetVersions(
        string planId,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? continuationToken = null)
    {
        if (pageSize <= 0 || pageSize > 200) pageSize = 25;

        var (items, next) = await _service.ListVersionsAsync(planId, TenantId, pageSize, continuationToken);
        return Ok(new PlanVersionPage { Items = items, ContinuationToken = next });
    }

    /// <summary>
    /// Get a single version by <c>VersionId</c>. Routes through the
    /// tenant-resolved <see cref="IBenefitPlanAdapter"/>.
    /// </summary>
    [HttpGet("{planId}/versions/{versionId}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BenefitPlan>> GetVersion(string planId, string versionId)
    {
        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetPlanVersionAsync(new BenefitPlanAdapterRequest
        {
            TenantId = TenantId,
            PlanId = planId,
            VersionId = versionId,
            PlatformSettings = settings,
        });

        if (response.Plan == null)
            return NotFound(new { message = $"Version '{versionId}' of plan '{planId}' not found" });
        return Ok(response.Plan.ToBenefitPlan());
    }

    /// <summary>Create a Draft v1 of a brand-new plan.</summary>
    [HttpPost("drafts")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BenefitPlan>> CreateDraft([FromBody] BenefitPlan plan)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try { PlanDocumentValidation.ValidateDocuments(plan.Documents); }
        catch (ArgumentException ex) { return BadRequest(new { field = ex.ParamName, message = ex.Message }); }

        try
        {
            var draft = await _service.CreateDraftAsync(plan, TenantId, ResolveActorId());
            return CreatedAtAction(nameof(GetVersion), new { planId = draft.PlanId, versionId = draft.VersionId }, draft);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
    }

    /// <summary>
    /// Move a Draft into Published. If a current Published version exists
    /// for the same <c>PlanId</c>, atomically supersedes it.
    /// </summary>
    [HttpPost("{planId}/versions/{versionId}/publish")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BenefitPlan>> Publish(
        string planId, string versionId, [FromBody] PublishRequest? body = null)
    {
        try
        {
            var published = await _service.PublishVersionAsync(
                planId, versionId, TenantId, ResolveActorId(), body?.EffectiveDate);
            return Ok(published);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Clone the latest Published version into a new Draft (next
    /// <c>VersionNumber</c>, predecessor link set).
    /// </summary>
    [HttpPost("{planId}/amend")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BenefitPlan>> Amend(string planId)
    {
        try
        {
            var draft = await _service.AmendPublishedPlanAsync(planId, TenantId, ResolveActorId());
            return CreatedAtAction(nameof(GetVersion), new { planId = draft.PlanId, versionId = draft.VersionId }, draft);
        }
        catch (PlanLimitValidationException ex)
        {
            return BadRequest(PlanLimitValidationPayload(ex));
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Terminates a Published version with no successor -- the standalone
    /// counterpart to the supersede-via-Publish path in
    /// <see cref="Publish"/>. <c>SupersededByVersionId</c> stays null,
    /// distinguishing "ended" from "replaced by an amendment".
    /// </summary>
    [HttpPost("{planId}/versions/{versionId}/supersede")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BenefitPlan>> Supersede(
        string planId, string versionId, [FromBody] SupersedeRequest body)
    {
        try
        {
            var result = await _service.SupersedeVersionAsync(
                planId, versionId, TenantId, ResolveActorId(), body?.Reason ?? string.Empty,
                body?.EffectiveDate ?? DateTime.UtcNow);
            return Ok(result);
        }
        catch (PlanVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (PlanVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, planId = ex.PlanId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header.ToString()))
            return header.ToString();
        return "system";
    }

    private static object PlanLimitValidationPayload(PlanLimitValidationException ex) => new
    {
        message = ex.Message,
        code = "PLAN_LIMIT_ACA_VIOLATION",
        planId = ex.PlanId,
        versionId = ex.VersionId,
        planYear = ex.PlanYear,
        field = ex.Field,
        supplied = ex.Supplied,
        cap = ex.Cap
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>Body for POST <c>/{planId}/versions/{versionId}/publish</c>.</summary>
public sealed class PublishRequest
{
    public DateTime? EffectiveDate { get; set; }
}

/// <summary>Body for POST <c>/{planId}/versions/{versionId}/supersede</c>.</summary>
public sealed class SupersedeRequest
{
    public string? Reason { get; set; }
    public DateTime? EffectiveDate { get; set; }
}

/// <summary>Page envelope for <c>GET /{planId}/versions</c>.</summary>
public sealed class PlanVersionPage
{
    public IReadOnlyList<BenefitPlan> Items { get; set; } = Array.Empty<BenefitPlan>();
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Response DTO matching the shape that eligibility-service AccumulationDto deserialises.
/// Property names must match EligibilityService.Models.DeductibleInfo / OutOfPocketInfo.
/// </summary>
public class AccumulationResponse
{
    public AccumulationDeductibleInfo? Deductible { get; set; }
    public AccumulationOutOfPocketInfo? OutOfPocket { get; set; }
}

public class AccumulationDeductibleInfo
{
    public decimal IndividualDeductible { get; set; }
    public decimal IndividualDeductibleMet { get; set; }
    public decimal IndividualDeductibleRemaining { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal FamilyDeductibleMet { get; set; }
    public decimal FamilyDeductibleRemaining { get; set; }
    public string TimePeriod { get; set; } = "Year";
}

public class AccumulationOutOfPocketInfo
{
    public decimal IndividualOOPMax { get; set; }
    public decimal IndividualOOPMet { get; set; }
    public decimal IndividualOOPRemaining { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal FamilyOOPMet { get; set; }
    public decimal FamilyOOPRemaining { get; set; }
    public string TimePeriod { get; set; } = "Year";
}

/// <summary>
/// Read-only DTO matching the existing Accumulators collection shape
/// (seeded by seed-demo-data.js, written by BenefitEngine during adjudication).
/// </summary>
public class AccumulatorDoc
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string BenefitPlanId { get; set; } = string.Empty;
    public string PlanYear { get; set; } = string.Empty;
    public List<AccumulatorBalanceDoc> Balances { get; set; } = new();
}

public class AccumulatorBalanceDoc
{
    public string Type { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public decimal LimitAmount { get; set; }
    public decimal AccumulatedAmount { get; set; }
}
