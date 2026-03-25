using Microsoft.AspNetCore.Mvc;
using CapitationService.Models;
using CapitationService.Services;

namespace CapitationService.Controllers;

[ApiController]
[Route("api/v1/capitation/disbursements")]
[Produces("application/json")]
public class CapitationDisbursementsController : ControllerBase
{
    private readonly ICapitationDisbursementService _disbursementService;
    private readonly ILogger<CapitationDisbursementsController> _logger;

    public CapitationDisbursementsController(
        ICapitationDisbursementService disbursementService,
        ILogger<CapitationDisbursementsController> logger)
    {
        _disbursementService = disbursementService;
        _logger = logger;
    }

    /// <summary>
    /// Initiate a disbursement for a single capitation statement
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CapitationDisbursement), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationDisbursement>> InitiateDisbursement([FromBody] InitiateDisbursementRequest request)
    {
        try
        {
            var disbursement = await _disbursementService.InitiateDisbursementAsync(request);
            return CreatedAtAction(nameof(GetDisbursementById), new { id = disbursement.Id }, disbursement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Initiate disbursements for a batch of statements (from capitation run or statement list)
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchDisbursementResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BatchDisbursementResult>> InitiateBatchDisbursement([FromBody] InitiateBatchDisbursementRequest request)
    {
        try
        {
            var result = await _disbursementService.InitiateBatchDisbursementAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generate a NACHA credit file for all pending NACHA disbursements
    /// </summary>
    [HttpPost("nacha-file")]
    [ProducesResponseType(typeof(NachaCreditFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NachaCreditFileResult>> GenerateNachaCreditFile()
    {
        try
        {
            var result = await _disbursementService.GenerateNachaCreditFileAsync();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get disbursement by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CapitationDisbursement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitationDisbursement>> GetDisbursementById(string id)
    {
        var disbursement = await _disbursementService.GetDisbursementByIdAsync(id);
        if (disbursement == null)
            return NotFound(new { error = $"Disbursement {id} not found" });
        return Ok(disbursement);
    }

    /// <summary>
    /// Get all disbursements for a statement
    /// </summary>
    [HttpGet("by-statement/{statementId}")]
    [ProducesResponseType(typeof(IEnumerable<CapitationDisbursement>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CapitationDisbursement>>> GetDisbursementsByStatement(string statementId)
    {
        var disbursements = await _disbursementService.GetDisbursementsByStatementAsync(statementId);
        return Ok(disbursements);
    }

    /// <summary>
    /// Cancel a pending disbursement
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(CapitationDisbursement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationDisbursement>> CancelDisbursement(string id)
    {
        try
        {
            var disbursement = await _disbursementService.CancelDisbursementAsync(id);
            return Ok(disbursement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Process an ACH return (bank rejection of credit)
    /// </summary>
    [HttpPost("returns")]
    [ProducesResponseType(typeof(CapitationDisbursement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationDisbursement>> ProcessReturn([FromBody] ProcessReturnRequest request)
    {
        try
        {
            var disbursement = await _disbursementService.ProcessReturnAsync(request);
            return Ok(disbursement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Stripe Connect webhook endpoint for transfer/payout events
    /// </summary>
    [HttpPost("stripe-webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> StripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers["Stripe-Signature"].FirstOrDefault();

        if (string.IsNullOrEmpty(stripeSignature))
            return BadRequest(new { error = "Missing Stripe-Signature header" });

        try
        {
            await _disbursementService.ProcessStripeWebhookAsync(json, stripeSignature);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe Connect webhook");
            return BadRequest(new { error = ex.Message });
        }
    }
}
