using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

[ApiController]
[Route("api/v1/premium-invoices")]
[Produces("application/json")]
public class PremiumInvoicesController : ControllerBase
{
    private readonly IPremiumBillingService _billingService;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly ILogger<PremiumInvoicesController> _logger;

    public PremiumInvoicesController(
        IPremiumBillingService billingService,
        IPremiumInvoiceRepository invoiceRepository,
        ILogger<PremiumInvoicesController> logger)
    {
        _billingService = billingService;
        _invoiceRepository = invoiceRepository;
        _logger = logger;
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Void an invoice
    /// </summary>
    [HttpPost("{id}/void")]
    [ProducesResponseType(typeof(PremiumInvoice), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PremiumInvoice>> VoidInvoice(string id, [FromBody] VoidInvoiceRequest request)
    {
        try
        {
            var invoice = await _billingService.VoidInvoiceAsync(id, request.Reason);
            return Ok(invoice);
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ProcessDelinquencies()
    {
        var count = await _billingService.ProcessDelinquenciesAsync();
        return Ok(new { delinquentCount = count, message = $"{count} invoices marked delinquent" });
    }
}

public class VoidInvoiceRequest
{
    public string Reason { get; set; } = string.Empty;
}
