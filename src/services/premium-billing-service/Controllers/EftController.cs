using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

/// <summary>
/// EFT/ACH auto-debit of sponsors. Releasing a debit (initiating drafts, which
/// for Stripe and batches submits them at once, and generating NACHA debit
/// files) needs payments:approve from a user who did not prepare the invoice
/// (maker-checker, see DebitSeparationOfDuties). NACHA files go from this
/// service straight to the bank; no response carries a file or a full number.
/// An undelivered file is held encrypted: a platform admin may retrieve it
/// (audited, with a reason) and another approver may retry it, never the
/// user who released it. Settlement, returns and
/// cancellation change the ledger and need finance:write. Reads need
/// billing:read or payments:read. The Stripe webhook is anonymous and
/// authenticated by its Stripe signature.
/// </summary>
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
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(EftDraft), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EftDraft>> InitiateDraft([FromBody] InitiateEftDraftRequest request)
    {
        try
        {
            var draft = await _eftDraftService.InitiateDraftAsync(request);
            return CreatedAtAction(nameof(GetDraftById), new { id = draft.Id }, draft);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
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
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(BatchEftResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BatchEftResult>> InitiateBatchDraft([FromBody] InitiateBatchEftRequest request)
    {
        try
        {
            var result = await _eftDraftService.InitiateBatchDraftAsync(request);
            return Ok(result);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generate a NACHA debit file for all pending NACHA drafts and send it
    /// straight to the tenant's bank (SFTP). Returns a masked summary (sponsor,
    /// last 4, amount per entry) and the transmission receipt, never the file.
    /// Drafts become Submitted only once the bank has the file; when it cannot
    /// be sent they are AwaitingRetrieval (file held encrypted for 7 days).
    /// </summary>
    [HttpPost("nacha/generate")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(NachaFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NachaFileResult>> GenerateNachaFile()
    {
        try
        {
            var result = await _eftDraftService.GenerateNachaFileForPendingDraftsAsync();
            return Ok(result);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// NACHA files that were not delivered to the bank and wait for a platform
    /// admin's retrieval or another approver's retry. Never the file.
    /// </summary>
    [HttpGet("nacha/held")]
    [RequirePermission("billing:read,payments:read,payments:approve")]
    [ProducesResponseType(typeof(IEnumerable<NachaHeldFileView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<NachaHeldFileView>>> ListHeldNachaFiles()
        => Ok((await _eftDraftService.ListHeldNachaFilesAsync()).Select(NachaHeldFileView.From));

    /// <summary>
    /// Send a held NACHA file to the bank again. payments:approve, a user token,
    /// and not the user who released it.
    /// </summary>
    [HttpPost("nacha/held/{fileReference}/retry")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(NachaFileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<NachaFileResult>> RetryNachaTransmission(string fileReference)
    {
        try
        {
            return Ok(await _eftDraftService.RetryNachaTransmissionAsync(fileReference));
        }
        catch (Exception ex) when (HeldFileProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>
    /// A held NACHA file, for a platform admin to deliver by hand: platform:admin,
    /// a user token, not the user who released it, and a reason. Every retrieval
    /// is recorded and audit-logged. The first retrieval marks its drafts Submitted.
    /// </summary>
    [HttpPost("nacha/held/{fileReference}/retrieve")]
    [RequirePermission("platform:admin")]
    [Produces("text/plain", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult> RetrieveHeldNachaFile(string fileReference, [FromBody] RetrieveNachaFileRequest request)
    {
        try
        {
            var file = await _eftDraftService.RetrieveHeldNachaFileAsync(fileReference, request?.Reason ?? string.Empty);
            Response.Headers.CacheControl = "no-store";
            return File(NachaFileFacts.Encode(file.Content), "text/plain", file.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (HeldFileProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private ActionResult? HeldFileProblem(Exception ex) => ex switch
    {
        NachaSeparationOfDutiesException => Problem(title: "Separation of duties", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden),
        NachaHeldFileNotFoundException => NotFound(new { error = ex.Message }),
        NachaHeldFileExpiredException => StatusCode(StatusCodes.Status410Gone, new { error = ex.Message }),
        NachaHeldFileStateException => Conflict(new { error = ex.Message }),
        _ => null
    };

    /// <summary>
    /// Get EFT draft by ID
    /// </summary>
    [HttpGet("drafts/{id}")]
    [RequirePermission("billing:read,payments:read")]
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
    [RequirePermission("billing:read,payments:read")]
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
    [RequirePermission("finance:write")]
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
    [RequirePermission("finance:write")]
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
    [RequirePermission("finance:write")]
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
    // Called by Stripe, which has no CHO token: authenticated by the
    // Stripe-Signature check in the service; the tenant comes from the signed
    // event's PaymentIntent metadata. Returns no tenant data.
    [AllowAnonymous]
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

    private ObjectResult SeparationOfDuties(SeparationOfDutiesException ex)
        => Problem(title: "Separation of duties", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
}
