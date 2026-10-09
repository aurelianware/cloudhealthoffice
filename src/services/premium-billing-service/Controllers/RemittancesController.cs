using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Edi;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Inbound premium payments: X12 820 files and bank lockbox files are parsed
/// and applied to open invoices; what cannot be matched lands in the
/// exceptions queue. Posting and resolving cash needs finance:write; reads
/// need billing:read (Program.cs default). The request body is the file itself.
/// </summary>
[ApiController]
[Route("api/v1/remittances")]
[Produces("application/json")]
public class RemittancesController : ControllerBase
{
    /// <summary>Largest file accepted (5 MB).</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    private readonly ICashApplicationService _cash;
    private readonly IRemittanceBatchRepository _batches;
    private readonly IRemittanceExceptionRepository _exceptions;
    private readonly ILogger<RemittancesController> _logger;

    public RemittancesController(
        ICashApplicationService cash,
        IRemittanceBatchRepository batches,
        IRemittanceExceptionRepository exceptions,
        ILogger<RemittancesController> logger)
    {
        _cash = cash;
        _batches = batches;
        _exceptions = exceptions;
        _logger = logger;
    }

    /// <summary>Upload an X12 820 (005010X218) file and apply its payments.</summary>
    [HttpPost("820")]
    [RequirePermission("finance:write")]
    [Consumes("text/plain", "application/edi-x12", "application/octet-stream")]
    [ProducesResponseType(typeof(RemittanceUploadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public Task<ActionResult<RemittanceUploadResult>> Upload820([FromQuery] string? fileName = null) =>
        UploadAsync(content => Edi820Parser.Parse(content, fileName), "820");

    /// <summary>Upload a lockbox CSV file and apply its checks.</summary>
    [HttpPost("lockbox")]
    [RequirePermission("finance:write")]
    [Consumes("text/csv", "text/plain", "application/octet-stream")]
    [ProducesResponseType(typeof(RemittanceUploadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public Task<ActionResult<RemittanceUploadResult>> UploadLockbox([FromQuery] string? fileName = null) =>
        UploadAsync(content => LockboxCsvParser.Parse(content, fileName), "lockbox");

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RemittanceBatch>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RemittanceBatch>>> Search(
        [FromQuery] DateTime? receivedFrom, [FromQuery] DateTime? receivedTo, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(await _batches.SearchAsync(receivedFrom, receivedTo, page, Math.Clamp(pageSize, 1, 200)));

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RemittanceBatch), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemittanceBatch>> GetById(string id)
    {
        var batch = await _batches.GetByIdAsync(id);
        return batch == null ? NotFound(new { error = $"Remittance {id} not found" }) : Ok(batch);
    }

    /// <summary>The cash exceptions queue (default: open items).</summary>
    [HttpGet("exceptions")]
    [ProducesResponseType(typeof(IEnumerable<RemittanceException>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RemittanceException>>> ListExceptions(
        [FromQuery] RemittanceExceptionStatus? status = RemittanceExceptionStatus.Open, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(await _exceptions.ListAsync(status, page, Math.Clamp(pageSize, 1, 200)));

    /// <summary>Apply an exception to an invoice, credit it to a sponsor account, or dismiss it.</summary>
    [HttpPost("exceptions/{id}/resolve")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(RemittanceException), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemittanceException>> ResolveException(string id, [FromBody] ResolveRemittanceExceptionRequest request)
    {
        try
        {
            return Ok(await _cash.ResolveExceptionAsync(id, request));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<ActionResult<RemittanceUploadResult>> UploadAsync(Func<string, IReadOnlyList<RemittanceAdvice>> parse, string kind)
    {
        var content = await ReadBodyAsync();
        if (content == null)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"The file is larger than {MaxFileBytes} bytes" });

        IReadOnlyList<RemittanceAdvice> advices;
        try
        {
            advices = parse(content);
        }
        catch (FormatException ex)
        {
            // Nothing from a file that does not parse is posted.
            return BadRequest(new { error = $"The {kind} file could not be read: {ex.Message}" });
        }

        var result = new RemittanceUploadResult();
        foreach (var advice in advices)
        {
            try
            {
                result.Batches.Add(await _cash.ApplyAsync(advice));
            }
            catch (DuplicateRemittanceException ex)
            {
                result.Duplicates.Add(new DuplicateRemittance { TraceNumber = advice.TraceNumber, PayerId = advice.PayerId, BatchId = ex.BatchId });
            }
            catch (ArgumentException ex)
            {
                result.Rejected.Add(new RejectedRemittance { TraceNumber = advice.TraceNumber, PayerId = advice.PayerId, Error = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One payment failing part-way must not lose the others in the file. Its batch stays
                // Processing with the items done so far; uploading the file again resumes it.
                _logger.LogError(ex, "{Kind} payment with trace {Trace} failed part-way; re-upload resumes it",
                    kind, advice.TraceNumber.Replace("\r", string.Empty).Replace("\n", string.Empty));
                result.Rejected.Add(new RejectedRemittance
                {
                    TraceNumber = advice.TraceNumber,
                    PayerId = advice.PayerId,
                    Error = "Posting failed part-way; upload the file again to resume this payment (items already applied are not applied twice)"
                });
            }
        }

        _logger.LogInformation("{Kind} upload: {Posted} payment(s) recorded, {Duplicates} duplicate(s), {Rejected} rejected",
            kind, result.Batches.Count, result.Duplicates.Count, result.Rejected.Count);
        return Ok(result);
    }

    /// <summary>The request body as text, or null when it is too large.</summary>
    private async Task<string?> ReadBodyAsync()
    {
        if (Request.ContentLength > MaxFileBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Sponsor receivable accounts: open invoice balance and unapplied credit.</summary>
[ApiController]
[Route("api/v1/sponsor-accounts")]
[Produces("application/json")]
public class SponsorAccountsController : ControllerBase
{
    private readonly ISponsorAccountRepository _accounts;
    private readonly IPremiumInvoiceRepository _invoices;

    public SponsorAccountsController(ISponsorAccountRepository accounts, IPremiumInvoiceRepository invoices)
    {
        _accounts = accounts;
        _invoices = invoices;
    }

    [HttpGet("{groupNumber}")]
    [ProducesResponseType(typeof(SponsorAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SponsorAccount>> Get(string groupNumber)
    {
        var account = await _accounts.GetAsync(groupNumber);
        return account == null ? NotFound(new { error = $"No account activity for group {groupNumber}" }) : Ok(account);
    }

    /// <summary>Recompute the open invoice balance from the group's invoices.</summary>
    [HttpPost("{groupNumber}/refresh")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(SponsorAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SponsorAccount>> Refresh(string groupNumber)
    {
        // Refreshing an unknown group would create an empty account for a typo.
        if (!await SponsorAccountBalances.GroupExistsAsync(_invoices, _accounts, groupNumber))
            return NotFound(new { error = $"Group {groupNumber} has no invoices or account" });
        return Ok(await SponsorAccountBalances.RefreshAsync(_invoices, _accounts, groupNumber, null));
    }
}

public class RemittanceUploadResult
{
    public List<RemittanceBatch> Batches { get; set; } = new();
    public List<DuplicateRemittance> Duplicates { get; set; } = new();
    public List<RejectedRemittance> Rejected { get; set; } = new();
}

public class DuplicateRemittance
{
    public string TraceNumber { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
}

public class RejectedRemittance
{
    public string TraceNumber { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
