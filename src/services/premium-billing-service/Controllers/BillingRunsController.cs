using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Billing runs. Reads need billing:read and writes billing:run (Program.cs
/// defaults); the tenant and the acting user come from the CHO token.
/// </summary>
[ApiController]
[Route("api/v1/billing-runs")]
[Produces("application/json")]
public class BillingRunsController : ControllerBase
{
    private readonly IPremiumBillingService _billingService;
    private readonly ICurrentActor _actor;
    private readonly ILogger<BillingRunsController> _logger;

    public BillingRunsController(
        IPremiumBillingService billingService,
        ICurrentActor actor,
        ILogger<BillingRunsController> logger)
    {
        _billingService = billingService;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Create a new billing run (does not execute)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BillingRun), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BillingRun>> CreateBillingRun([FromBody] CreateBillingRunRequest request)
    {
        _logger.LogInformation("Creating billing run for period {BillingPeriod}", request.BillingPeriod);

        // The creator is the token subject; request.CreatedBy is never read from the body.
        var billingRun = await _billingService.CreateBillingRunAsync(request, _actor.UserId);

        return CreatedAtAction(
            nameof(GetBillingRunById),
            new { id = billingRun.Id },
            billingRun);
    }

    /// <summary>
    /// Execute an existing billing run (generates invoices for all matching sponsors)
    /// </summary>
    [HttpPost("{id}/execute")]
    [ProducesResponseType(typeof(BillingRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BillingRun>> ExecuteBillingRun(string id)
    {
        _logger.LogInformation("Executing billing run {BillingRunId}", SanitizeForLog(id));

        try
        {
            var billingRun = await _billingService.ExecuteBillingRunAsync(id);
            return Ok(billingRun);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Create and immediately execute a billing run
    /// </summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(BillingRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BillingRun>> CreateAndExecuteBillingRun([FromBody] CreateBillingRunRequest request)
    {
        _logger.LogInformation("Creating and executing billing run for period {BillingPeriod}", request.BillingPeriod);

        var billingRun = await _billingService.CreateBillingRunAsync(request, _actor.UserId);
        var executed = await _billingService.ExecuteBillingRunAsync(billingRun.Id);

        return Ok(executed);
    }

    /// <summary>
    /// Get billing run by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(BillingRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingRun>> GetBillingRunById(string id)
    {
        try
        {
            var billingRun = await _billingService.GetBillingRunAsync(id);
            return Ok(billingRun);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List billing runs with optional date range filter
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<BillingRun>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BillingRun>>> GetBillingRuns(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var billingRuns = await _billingService.GetBillingRunsAsync(from, to);
        return Ok(billingRuns);
    }

    /// <summary>
    /// Cancel a pending billing run
    /// </summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CancelBillingRun(string id)
    {
        try
        {
            await _billingService.CancelBillingRunAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
