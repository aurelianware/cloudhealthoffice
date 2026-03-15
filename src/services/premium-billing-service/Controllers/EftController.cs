using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

[ApiController]
[Route("api/v1/eft")]
[Produces("application/json")]
public class EftController : ControllerBase
{
    private readonly IEftDraftService _eftDraftService;
    private readonly ILogger<EftController> _logger;

    public EftController(IEftDraftService eftDraftService, ILogger<EftController> logger)
    {
        _eftDraftService = eftDraftService;
        _logger = logger;
    }

    /// <summary>
    /// Initiate an EFT/ACH draft for a single invoice
    /// </summary>
    [HttpPost("drafts")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EftDraft>> InitiateDraft([FromBody] InitiateEftDraftRequest request)
    {
        try
        {
            var draft = await _eftDraftService.InitiateDraftAsync(request);
            return CreatedAtAction(nameof(GetDraftById), new { id = draft.Id }, draft);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Initiate EFT drafts for a batch of invoices (from billing run or invoice list)
    /// </summary>
    [HttpPost("drafts/batch")]
    [ProducesResponseType(typeof(BatchEftResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BatchEftResult>> InitiateBatchDraft([FromBody] InitiateBatchEftRequest request)
    {
        try
        {
            var result = await _eftDraftService.InitiateBatchDraftAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generate a NACHA file for all pending NACHA drafts
    /// </summary>
    [HttpPost("nacha/generate")]
    [ProducesResponseType(typeof(NachaFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NachaFileResult>> GenerateNachaFile()
    {
        try
        {
            var result = await _eftDraftService.GenerateNachaFileForPendingDraftsAsync();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generate a NACHA file for all pending drafts and return it as a downloadable file.
    /// This endpoint has side effects: it marks pending drafts as submitted.
    /// Use POST /nacha/generate if you only need the file metadata.
    /// </summary>
    [HttpPost("nacha/generate-and-download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> GenerateAndDownloadNachaFile()
    {
        try
        {
            var result = await _eftDraftService.GenerateNachaFileForPendingDraftsAsync();
            var bytes = System.Text.Encoding.ASCII.GetBytes(result.FileContent);
            return File(bytes, "text/plain", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get EFT draft by ID
    /// </summary>
    [HttpGet("drafts/{id}")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EftDraft>> GetDraftById(string id)
    {
        var draft = await _eftDraftService.GetDraftByIdAsync(id);
        if (draft == null)
            return NotFound(new { error = $"Draft {id} not found" });
        return Ok(draft);
    }

    /// <summary>
    /// Get all EFT drafts for an invoice
    /// </summary>
    [HttpGet("drafts/invoice/{invoiceId}")]
    [ProducesResponseType(typeof(IEnumerable<EftDraft>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EftDraft>>> GetDraftsByInvoice(string invoiceId)
    {
        var drafts = await _eftDraftService.GetDraftsByInvoiceAsync(invoiceId);
        return Ok(drafts);
    }

    /// <summary>
    /// Mark a draft as settled (for NACHA drafts confirmed by bank)
    /// </summary>
    [HttpPost("drafts/{id}/settle")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EftDraft>> SettleDraft(string id)
    {
        try
        {
            var draft = await _eftDraftService.SettleDraftAsync(id);
            return Ok(draft);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Process an ACH return (bank rejection)
    /// </summary>
    [HttpPost("drafts/returns")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EftDraft>> ProcessAchReturn([FromBody] ProcessAchReturnRequest request)
    {
        try
        {
            var draft = await _eftDraftService.ProcessAchReturnAsync(request);
            return Ok(draft);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel a pending EFT draft
    /// </summary>
    [HttpPost("drafts/{id}/cancel")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EftDraft>> CancelDraft(string id)
    {
        try
        {
            var draft = await _eftDraftService.CancelDraftAsync(id);
            return Ok(draft);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Stripe webhook endpoint for ACH payment events
    /// </summary>
    [HttpPost("webhooks/stripe")]
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
            await _eftDraftService.ProcessStripeWebhookAsync(json, stripeSignature);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook");
            return BadRequest(new { error = ex.Message });
        }
    }
}
