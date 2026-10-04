using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// Payment runs. Reads need payments:read; creating and cancelling a run
/// (preparation) need payments:run (Program.cs defaults). Executing a run
/// releases money (check numbers, Posted payments, 835 envelopes, claims
/// finalized as paid), so it needs payments:approve from a user who did not
/// create the run (maker-checker, RunSeparationOfDuties); a service token is
/// refused. The tenant and the acting user come from the CHO token.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PaymentRunsController : ControllerBase
{
    private readonly IPaymentRunService _paymentRunService;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PaymentRunsController> _logger;

    public PaymentRunsController(
        IPaymentRunService paymentRunService,
        ICurrentActor actor,
        ILogger<PaymentRunsController> logger)
    {
        _paymentRunService = paymentRunService;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Create a new payment run (does not execute)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> CreatePaymentRun([FromBody] CreatePaymentRunRequest request)
    {
        _logger.LogInformation("Creating payment run with criteria: LOB={LOB}, Provider={Provider}",
            request.Criteria.LineOfBusiness, SanitizeForLog(request.Criteria.ProviderNPI));

        // The creator is the token subject; request.CreatedBy is never read.
        var paymentRun = await _paymentRunService.CreatePaymentRunAsync(
            request.Criteria,
            _actor.UserId);

        return CreatedAtAction(
            nameof(GetPaymentRunById),
            new { id = paymentRun.Id },
            paymentRun);
    }

    /// <summary>
    /// Create and immediately execute a payment run. Always refused (403): the
    /// creator would be the executor, and releasing money needs a second user.
    /// Create the run with POST /api/paymentruns; a different user with
    /// payments:approve executes it with POST /api/paymentruns/{id}/execute.
    /// Nothing is created.
    /// </summary>
    [HttpPost("execute")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PaymentRun> CreateAndExecutePaymentRun([FromBody] CreatePaymentRunRequest request)
    {
        _logger.LogWarning("Refused create-and-execute payment run by {User}: the creator cannot execute",
            SanitizeForLog(_actor.UserId));
        return SeparationOfDuties(new SeparationOfDutiesException(
            "Separation of duties: a payment run cannot be created and executed by the same caller. " +
            "Create it with POST /api/paymentruns; a different user with payments:approve executes it."));
    }

    /// <summary>
    /// Execute an existing payment run
    /// </summary>
    [HttpPost("{id}/execute")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> ExecutePaymentRun(string id)
    {
        _logger.LogInformation("Executing payment run {PaymentRunId}", SanitizeForLog(id));

        try
        {
            var paymentRun = await _paymentRunService.ExecutePaymentRunAsync(id);
            return Ok(paymentRun);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get payment run by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentRun>> GetPaymentRunById(string id)
    {
        try
        {
            var paymentRun = await _paymentRunService.GetPaymentRunAsync(id);
            return Ok(paymentRun);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Get all payment runs with optional date filter
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PaymentRun>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PaymentRun>>> GetPaymentRuns(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var paymentRuns = await _paymentRunService.GetPaymentRunsAsync(from, to);
        return Ok(paymentRuns);
    }

    /// <summary>
    /// Cancel a pending payment run
    /// </summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CancelPaymentRun(string id)
    {
        try
        {
            await _paymentRunService.CancelPaymentRunAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private ObjectResult SeparationOfDuties(SeparationOfDutiesException ex)
        => Problem(title: "Separation of duties", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class CreatePaymentRunRequest
{
    public PaymentRunCriteria Criteria { get; set; } = new();
    /// <summary>Ignored: the creator is the token subject.</summary>
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}
