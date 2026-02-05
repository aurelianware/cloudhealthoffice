using Microsoft.AspNetCore.Mvc;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Services;

namespace BenefitPlanService.Controllers;

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
            TenantId, payer, planType, activeOnly);
        
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
        var plan = await _service.GetPlanAsync(id);
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

        var created = await _service.CreatePlanAsync(plan);
        return CreatedAtAction(nameof(GetPlan), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing benefit plan
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(BenefitPlan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BenefitPlan>> UpdatePlan(string id, [FromBody] BenefitPlan plan)
    {
        if (id != plan.Id)
        {
            return BadRequest(new { message = "ID mismatch" });
        }

        var updated = await _service.UpdatePlanAsync(plan);
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
        var deleted = await _service.DeletePlanAsync(id);
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
        var plan = await _service.GetPlanAsync(id);
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
        var added = await _service.AddBenefitAsync(id, benefit);
        if (added == null)
        {
            return NotFound(new { message = $"Benefit plan '{id}' not found" });
        }

        return CreatedAtAction(nameof(GetPlanBenefits), new { id }, added);
    }
}
