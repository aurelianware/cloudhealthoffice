using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PaymentRunsController : ControllerBase
{
    private readonly IPaymentRunService _paymentRunService;
    private readonly ILogger<PaymentRunsController> _logger;

    public PaymentRunsController(
        IPaymentRunService paymentRunService,
        ILogger<PaymentRunsController> logger)
    {
        _paymentRunService = paymentRunService;
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

        var paymentRun = await _paymentRunService.CreatePaymentRunAsync(
            request.Criteria, 
            request.CreatedBy);

        return CreatedAtAction(
            nameof(GetPaymentRunById),
            new { id = paymentRun.Id },
            paymentRun);
    }

    /// <summary>
    /// Create and immediately execute a payment run
    /// </summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> CreateAndExecutePaymentRun([FromBody] CreatePaymentRunRequest request)
    {
        _logger.LogInformation("Creating and executing payment run");

        var paymentRun = await _paymentRunService.CreatePaymentRunAsync(
            request.Criteria, 
            request.CreatedBy);

        var executed = await _paymentRunService.ExecutePaymentRunAsync(paymentRun.Id);

        return Ok(executed);
    }

    /// <summary>
    /// Execute an existing payment run
    /// </summary>
    [HttpPost("{id}/execute")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
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
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}
