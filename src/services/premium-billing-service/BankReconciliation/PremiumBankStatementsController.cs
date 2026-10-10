using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.BankReconciliation;

public enum PremiumSuspenseAction
{
    /// <summary>Apply the bank line to an invoice through cash application.</summary>
    ApplyToInvoice,

    /// <summary>The bank line is money already recorded (an 820 or lockbox payment): link it, post nothing.</summary>
    MarkMatched,

    /// <summary>Not premium cash, or handled elsewhere.</summary>
    Dismiss,
}

public sealed class ResolvePremiumSuspenseRequest
{
    public PremiumSuspenseAction Action { get; set; }

    /// <summary>ApplyToInvoice: the invoice id.</summary>
    public string? InvoiceId { get; set; }

    /// <summary>MarkMatched: the remittance batch ids the line confirms.</summary>
    public List<string> RemittanceBatchIds { get; set; } = [];

    /// <summary>Why; required.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// BAI2 bank statements for the premium deposit accounts: upload, the
/// reconciliation decisions, and the suspense work queue. Posting and
/// resolving need finance:write; reads need billing:read (service default).
/// </summary>
[ApiController]
[Route("api/v1/bank-statements")]
[Produces("application/json")]
public sealed class PremiumBankStatementsController : ControllerBase
{
    private readonly BankStatementIngestion _ingestion;
    private readonly BankReconciliationRunner _runner;
    private readonly IBankReconciliationStore _store;
    private readonly PremiumBankTransactionMatcher _matcher;
    private readonly IPremiumInvoiceRepository _invoices;
    private readonly IRemittanceBatchRepository _batches;
    private readonly ICurrentActor _actor;
    private readonly BankReconciliationOptions _options;
    private readonly BankStatementSupersessionService _supersessions;

    public PremiumBankStatementsController(
        BankStatementIngestion ingestion,
        BankReconciliationRunner runner,
        IBankReconciliationStore store,
        PremiumBankTransactionMatcher matcher,
        IPremiumInvoiceRepository invoices,
        IRemittanceBatchRepository batches,
        ICurrentActor actor,
        IOptions<BankReconciliationOptions> options,
        BankStatementSupersessionService supersessions)
    {
        _supersessions = supersessions;
        _ingestion = ingestion;
        _runner = runner;
        _store = store;
        _matcher = matcher;
        _invoices = invoices;
        _batches = batches;
        _actor = actor;
        _options = options.Value;
    }

    /// <summary>Upload a BAI2 file (the body). A file whose control totals do not match is rejected whole (400).</summary>
    [HttpPost("bai2")]
    [RequirePermission("finance:write")]
    [Consumes("text/plain", "application/octet-stream")]
    [ProducesResponseType(typeof(BankStatementUploadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BankStatementUploadResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<BankStatementUploadResult>> Upload([FromQuery] string? fileName, CancellationToken ct)
    {
        var bytes = await BankStatementUpload.ReadBodyAsync(Request, _options.MaxFileBytes, ct);
        if (bytes is null)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"The file is larger than {_options.MaxFileBytes} bytes" });
        if (bytes.Length == 0)
            return BadRequest(new { error = "The request body must be a BAI2 file." });

        var ingest = await _ingestion.IngestAsync(_actor.TenantId, bytes, fileName, _actor.UserId, ct);
        if (!ingest.IsAccepted)
            return BadRequest(BankStatementUploadResult.From(ingest, null));

        // A superseded statement carries no transactions: nothing is reconciled from it again.
        var summary = await _runner.RunAsync(ingest.Transactions, _matcher, ct);
        return Ok(BankStatementUploadResult.From(ingest, summary));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BankStatementFile>>> List([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok((await _store.ListFilesAsync(_actor.TenantId, limit, ct)).Select(f => f.WithoutContent()).ToList());

    [HttpGet("{id}")]
    public async Task<ActionResult<BankStatementFile>> Get(string id, CancellationToken ct) =>
        await _store.GetFileAsync(_actor.TenantId, id, ct) is { } f ? Ok(f.WithoutContent()) : NotFound(new { error = $"Bank statement {id} not found" });

    /// <summary>Reconciliation decisions; <c>status=Suspense</c> (and <c>Return</c>) is the work queue.</summary>
    public sealed class SupersedeRequest
    {
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Step 1 of replacing this statement with a corrected file that reuses the
    /// bank's file id: ask, with a reason. Refused once any line was applied,
    /// matched or settled.
    /// </summary>
    [HttpPost("{id}/supersede")]
    [RequirePermission("finance:write")]
    public async Task<ActionResult<BankStatementSupersession>> RequestSupersession(string id, [FromBody] SupersedeRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _supersessions.RequestAsync(_actor.TenantId, id, request.Reason, _actor.UserId, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Step 2: a second person approves; the corrected file is then accepted and this statement's open lines are dismissed.</summary>
    [HttpPost("{id}/supersede/approve")]
    [RequirePermission("finance:write")]
    public async Task<ActionResult<BankStatementSupersession>> ApproveSupersession(string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _supersessions.ApproveAsync(_actor.TenantId, id, _actor.UserId, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{id}/supersession")]
    public async Task<ActionResult<BankStatementSupersession>> GetSupersession(string id, CancellationToken ct) =>
        await _supersessions.GetAsync(_actor.TenantId, id, ct) is { } s ? Ok(s) : NotFound(new { error = $"No supersession for statement {id}" });

    [HttpGet("items")]
    public async Task<ActionResult<IReadOnlyList<ReconciliationItem>>> Items(
        [FromQuery] ReconciliationStatus? status, [FromQuery] string? statementId, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(await _store.ListItemsAsync(_actor.TenantId, _options.ServiceName, status, statementId, limit, ct));

    /// <summary>Work a suspense or return item: apply it to an invoice, link it to recorded payments, or dismiss it.</summary>
    [HttpPost("items/{id}/resolve")]
    [RequirePermission("finance:write")]
    public async Task<ActionResult<ReconciliationItem>> Resolve(string id, [FromBody] ResolvePremiumSuspenseRequest request, CancellationToken ct)
    {
        try
        {
            var item = await _store.GetItemAsync(_actor.TenantId, id, ct)
                       ?? throw new KeyNotFoundException($"Reconciliation item {id} not found");
            if (!item.IsOpen)
                return BadRequest(new { error = $"Reconciliation item {id} is {item.Status}, not open" });
            if (string.IsNullOrWhiteSpace(request.Note))
                return BadRequest(new { error = "A note saying why is required." });

            switch (request.Action)
            {
                case PremiumSuspenseAction.ApplyToInvoice:
                {
                    if (item.Status == ReconciliationStatus.Return || item.Direction != CloudHealthOffice.BankReconciliation.Bai2.Bai2Direction.Credit)
                        return BadRequest(new { error = "Only a bank credit can be applied to an invoice." });
                    // A lockbox deposit total is the day's checks: they are applied
                    // from the lockbox file. Applying the total too would post them twice.
                    if (item.Kind == "LockboxDeposit")
                        return BadRequest(new { error = "A lockbox deposit is applied from its lockbox file, not to an invoice: load the lockbox file, then MarkMatched." });
                    var invoice = string.IsNullOrWhiteSpace(request.InvoiceId) ? null : await _invoices.GetByIdAsync(request.InvoiceId);
                    if (invoice is null) return BadRequest(new { error = "invoiceId must name an existing invoice." });
                    var tx = (await _store.GetTransactionsAsync(_actor.TenantId, item.StatementId, ct)).FirstOrDefault(t => t.Id == item.TransactionId)
                             ?? throw new KeyNotFoundException($"Bank transaction {item.TransactionId} not found");
                    var decision = await _matcher.ApplyAsync(tx, invoice);
                    return Ok(await _runner.ResolveAsync(_actor.TenantId, id, dismiss: false, request.Note, _actor.UserId,
                        decision.MatchedEntityType, decision.MatchedEntityIds, ct));
                }
                case PremiumSuspenseAction.MarkMatched:
                {
                    if (request.RemittanceBatchIds.Count == 0)
                        return BadRequest(new { error = "remittanceBatchIds is required." });
                    foreach (var batchId in request.RemittanceBatchIds)
                        if (await _batches.GetByIdAsync(batchId) is null)
                            return BadRequest(new { error = $"Remittance batch {batchId} not found." });
                    return Ok(await _runner.ResolveAsync(_actor.TenantId, id, dismiss: false, request.Note, _actor.UserId,
                        "RemittanceBatch", request.RemittanceBatchIds, ct));
                }
                default:
                    return Ok(await _runner.ResolveAsync(_actor.TenantId, id, dismiss: true, request.Note, _actor.UserId, ct: ct));
            }
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
}
