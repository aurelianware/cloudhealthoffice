using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Premium invoices. Reads need billing:read and writes billing:run (Program.cs
/// defaults). Ledger changes (payments, voids) and delinquency processing (which
/// suspends sponsors) need finance:write. Tenant and actor come from the token.
/// </summary>
[ApiController]
[Route("api/v1/premium-invoices")]
[Produces("application/json")]
public class PremiumInvoicesController : ControllerBase
{
    private readonly IPremiumBillingService _billingService;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly ILogger<PremiumInvoicesController> _logger;
    private readonly IRatedInvoiceGenerator? _ratedInvoices;
    private readonly ICurrentActor? _actor;

    public PremiumInvoicesController(
        IPremiumBillingService billingService,
        IPremiumInvoiceRepository invoiceRepository,
        ILogger<PremiumInvoicesController> logger,
        IRatedInvoiceGenerator? ratedInvoices = null,
        ICurrentActor? actor = null)
    {
        _billingService = billingService;
        _invoiceRepository = invoiceRepository;
        _logger = logger;
        _ratedInvoices = ratedInvoices;
        _actor = actor;
    }

    /// <summary>
    /// Recompute a Draft rated invoice (after a missing rate table or member
    /// data was fixed). Issued invoices are never recomputed: 400.
    /// </summary>
    [HttpPost("{id}/regenerate")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PremiumInvoice>> RegenerateDraft(string id, CancellationToken cancellationToken)
    {
        if (_ratedInvoices == null || _actor == null)
            return BadRequest(new { error = "Rated billing is not available" });
        try
        {
            var outcome = await _ratedInvoices.RegenerateDraftAsync(id, _actor.TenantId, _actor.UserId, cancellationToken);
            return Ok(outcome.Invoice);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ConcurrencyConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Issue a Draft rated invoice that has no rating exceptions (tenants that
    /// hold rated invoices for review). Its lines are fixed from then on.
    /// </summary>
    [HttpPost("{id}/issue")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PremiumInvoice>> IssueDraft(string id)
    {
        if (_ratedInvoices == null || _actor == null)
            return BadRequest(new { error = "Rated billing is not available" });
        try
        {
            return Ok(await _ratedInvoices.IssueDraftAsync(id, _actor.UserId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ConcurrencyConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Search invoices with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PremiumInvoice>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PremiumInvoice>>> SearchInvoices(
        [FromQuery] string? groupNumber,
        [FromQuery] DateTime? periodFrom,
        [FromQuery] DateTime? periodTo,
        [FromQuery] InvoiceStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var invoices = await _invoiceRepository.SearchAsync(groupNumber, periodFrom, periodTo, status, page, pageSize);
        return Ok(invoices);
    }

    /// <summary>
    /// Get invoice by ID (includes line items, adjustments, and payments)
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PremiumInvoice>> GetInvoiceById(string id)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(id);
        if (invoice == null)
            return NotFound(new { error = $"Invoice {id} not found" });
        return Ok(invoice);
    }

    /// <summary>
    /// Get all invoices for a sponsor group
    /// </summary>
    [HttpGet("sponsor/{groupNumber}")]
    [ProducesResponseType(typeof(IEnumerable<PremiumInvoice>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PremiumInvoice>>> GetInvoicesBySponsor(string groupNumber)
    {
        var invoices = await _invoiceRepository.GetByGroupNumberAsync(groupNumber);
        return Ok(invoices);
    }

    /// <summary>
    /// Record a payment against an invoice
    /// </summary>
    [HttpPost("{id}/payments")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PremiumInvoice>> RecordPayment(string id, [FromBody] RecordPaymentRequest request)
    {
        try
        {
            var invoice = await _billingService.RecordPaymentAsync(id, request);
            return Ok(invoice);
        }
        catch (ConcurrencyConflictException ex)
        {
            // Kept changing under us after retries: the caller may retry.
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Void an invoice
    /// </summary>
    [HttpPost("{id}/void")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PremiumInvoice>> VoidInvoice(string id, [FromBody] VoidInvoiceRequest request)
    {
        try
        {
            var invoice = await _billingService.VoidInvoiceAsync(id, request.Reason);
            return Ok(invoice);
        }
        catch (ConcurrencyConflictException ex)
        {
            // Kept changing under us after retries: the caller may retry.
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark an invoice as sent to the sponsor
    /// </summary>
    [HttpPost("{id}/send")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PremiumInvoice>> MarkInvoiceSent(string id)
    {
        try
        {
            var invoice = await _billingService.MarkInvoiceSentAsync(id);
            return Ok(invoice);
        }
        catch (ConcurrencyConflictException ex)
        {
            // Kept changing under us after retries: the caller may retry.
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all overdue invoices
    /// </summary>
    [HttpGet("overdue")]
    [ProducesResponseType(typeof(IEnumerable<PremiumInvoice>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PremiumInvoice>>> GetOverdueInvoices()
    {
        var invoices = await _billingService.GetOverdueInvoicesAsync();
        return Ok(invoices);
    }

    /// <summary>
    /// Get aging report (current, 30, 60, 90+ day buckets)
    /// </summary>
    [HttpGet("aging-report")]
    [ProducesResponseType(typeof(AgingReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<AgingReport>> GetAgingReport()
    {
        var report = await _billingService.GetAgingReportAsync();
        return Ok(report);
    }

    /// <summary>
    /// Process delinquencies: mark invoices past grace period as delinquent and suspend sponsors
    /// </summary>
    [HttpPost("process-delinquencies")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> ProcessDelinquencies()
    {
        var result = await _billingService.ProcessDelinquenciesAsync();
        var body = new
        {
            delinquentCount = result.DelinquentCount,
            sponsorsSuspended = result.SponsorsSuspended,
            suspensionRetries = result.SuspensionRetries,
            suspensionFailures = result.SuspensionFailures,
            invoiceFailures = result.InvoiceFailures,
            skippedAfterConflict = result.SkippedAfterConflict,
            message = result.SuspensionFailures.Count == 0
                ? $"{result.DelinquentCount} invoices marked delinquent"
                : $"{result.DelinquentCount} invoices marked delinquent; {result.SuspensionFailures.Count} sponsor suspension(s) " +
                  "FAILED in sponsor-service (recorded on the invoices and retried on the next run)"
        };

        // A failed suspension is not a success: 502 tells a scheduler or the
        // portal that sponsor-service did not do what was asked. Invoices this
        // run could not process (the rest were) answer 207 so the run is not
        // mistaken for a complete success.
        if (result.SuspensionFailures.Count > 0)
            return StatusCode(StatusCodes.Status502BadGateway, body);
        return result.InvoiceFailures.Count == 0
            ? Ok(body)
            : StatusCode(StatusCodes.Status207MultiStatus, body);
    }
}

public class VoidInvoiceRequest
{
    public string Reason { get; set; } = string.Empty;
}
