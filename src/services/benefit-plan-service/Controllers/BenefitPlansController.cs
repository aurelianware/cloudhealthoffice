using Microsoft.AspNetCore.Mvc;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
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
    private readonly ILogger<BenefitPlansController> _logger;

    public BenefitPlansController(IBenefitPlanService service, ILogger<BenefitPlansController> logger)
    {
        _service = service;
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
    /// Get a specific benefit plan by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BenefitPlan>> GetPlan(string id)
    {
        var plan = await _service.GetPlanAsync(id, TenantId);
        if (plan == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }
        
        return Ok(plan);
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

        var created = await _service.CreatePlanAsync(plan, TenantId);
        return CreatedAtAction(nameof(GetPlan), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing benefit plan
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

        var updated = await _service.UpdatePlanAsync(plan, TenantId);
        if (updated == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return Ok(updated);
    }

    /// <summary>
    /// Delete a benefit plan (soft delete)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePlan(string id)
    {
        var deleted = await _service.DeletePlanAsync(id, TenantId);
        if (!deleted)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return NoContent();
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
    /// Add a benefit to a plan
    /// </summary>
    [HttpPost("{id}/benefits")]
    [ProducesResponseType(typeof(Benefit), StatusCodes.Status201Created)]
    public async Task<ActionResult<Benefit>> AddBenefit(string id, [FromBody] Benefit benefit)
    {
        var added = await _service.AddBenefitAsync(id, TenantId, benefit);
        if (added == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return CreatedAtAction(nameof(GetPlanBenefits), new { id }, added);
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

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
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
