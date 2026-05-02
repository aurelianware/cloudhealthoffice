using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// Operator-initiated 835 reversal batch surface (capability 5.12b).
/// Mirrors <c>PaymentRunsController</c> — the second instance of the
/// operator-initiated batch workflow pattern. Routes under
/// <c>/api/reversalruns</c> via <c>[Route("api/[controller]")]</c> for
/// parity with <c>/api/paymentruns</c> (no <c>/v1</c> prefix; pattern
/// parity per Plan-First Premise F).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ReversalRunsController : ControllerBase
{
    private readonly IReversalRunService _reversalRunService;
    private readonly ILogger<ReversalRunsController> _logger;

    public ReversalRunsController(
        IReversalRunService reversalRunService,
        ILogger<ReversalRunsController> logger)
    {
        _reversalRunService = reversalRunService;
        _logger = logger;
    }

    /// <summary>Create a new reversal run (does not execute).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ReversalRun), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReversalRun>> CreateReversalRun([FromBody] CreateReversalRunRequest request)
    {
        _logger.LogInformation(
            "Creating reversal run with criteria: Provider={Provider}",
            SanitizeForLog(request.Criteria.ProviderNPI));

        var run = await _reversalRunService.CreateReversalRunAsync(
            request.Criteria, request.CreatedBy, request.Description);
        return CreatedAtAction(nameof(GetReversalRunById), new { id = run.Id }, run);
    }

    /// <summary>Create and immediately execute a reversal run.</summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(ReversalRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReversalRun>> CreateAndExecuteReversalRun([FromBody] CreateReversalRunRequest request)
    {
        _logger.LogInformation("Creating and executing reversal run");
        var run = await _reversalRunService.CreateReversalRunAsync(
            request.Criteria, request.CreatedBy, request.Description);
        var executed = await _reversalRunService.ExecuteReversalRunAsync(run.Id);
        return Ok(executed);
    }

    /// <summary>Execute an existing reversal run (Pending → Running → Completed).</summary>
    [HttpPost("{id}/execute")]
    [ProducesResponseType(typeof(ReversalRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReversalRun>> ExecuteReversalRun(string id)
    {
        _logger.LogInformation("Executing reversal run {ReversalRunId}", SanitizeForLog(id));
        try
        {
            var run = await _reversalRunService.ExecuteReversalRunAsync(id);
            return Ok(run);
        }
        catch (InvalidOperationException ex) when (IsNotFound(ex))
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Get reversal run by ID.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ReversalRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReversalRun>> GetReversalRunById(string id)
    {
        try
        {
            var run = await _reversalRunService.GetReversalRunAsync(id);
            return Ok(run);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>Get all reversal runs with optional date filter.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ReversalRun>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ReversalRun>>> GetReversalRuns(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var runs = await _reversalRunService.GetReversalRunsAsync(from, to);
        return Ok(runs);
    }

    /// <summary>Cancel a pending reversal run.</summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CancelReversalRun(string id)
    {
        try
        {
            await _reversalRunService.CancelReversalRunAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex) when (IsNotFound(ex))
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // The service throws InvalidOperationException for both not-found
    // and state-violation cases. Distinguish via the message prefix the
    // service always emits ("Reversal run {id} not found") so the
    // controller can return 404 vs 400 to match the declared response
    // types.
    private static bool IsNotFound(InvalidOperationException ex) =>
        ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class CreateReversalRunRequest
{
    public ReversalRunCriteria Criteria { get; set; } = new();
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}
