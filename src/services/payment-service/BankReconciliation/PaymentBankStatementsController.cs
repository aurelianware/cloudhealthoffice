using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PaymentService.Repositories;

namespace PaymentService.BankReconciliation;

public enum PaymentSuspenseAction
{
    /// <summary>The bank line settles this NACHA file (a person checked it).</summary>
    MarkSettled,

    /// <summary>Handled (e.g. a return whose payment was reissued or voided); the note says how.</summary>
    Resolve,

    /// <summary>Not a NACHA settlement or return.</summary>
    Dismiss,
}

public sealed class ResolvePaymentSuspenseRequest
{
    public PaymentSuspenseAction Action { get; set; }

    /// <summary>MarkSettled: the NACHA file reference.</summary>
    public string? FileReference { get; set; }

    /// <summary>Resolve: what it was matched to (e.g. payment ids of a returned entry).</summary>
    public List<string> RelatedIds { get; set; } = [];

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// BAI2 statements for the disbursement account: NACHA settlement and ACH
/// returns. Reads need payments:read, writes payments:run (service defaults).
/// </summary>
[ApiController]
[Route("api/v1/bank-statements")]
[Produces("application/json")]
public sealed class PaymentBankStatementsController : ControllerBase
{
    private readonly BankStatementIngestion _ingestion;
    private readonly BankReconciliationRunner _runner;
    private readonly IBankReconciliationStore _store;
    private readonly NachaSettlementMatcher _matcher;
    private readonly INachaSettlementStore _settlements;
    private readonly IPaymentFileTransmissionRepository _transmissions;
    private readonly ICurrentActor _actor;
    private readonly BankReconciliationOptions _options;
    private readonly BankStatementSupersessionService _supersessions;

    public PaymentBankStatementsController(
        BankStatementIngestion ingestion,
        BankReconciliationRunner runner,
        IBankReconciliationStore store,
        NachaSettlementMatcher matcher,
        INachaSettlementStore settlements,
        IPaymentFileTransmissionRepository transmissions,
        ICurrentActor actor,
        IOptions<BankReconciliationOptions> options,
        BankStatementSupersessionService supersessions)
    {
        _supersessions = supersessions;
        _ingestion = ingestion;
        _runner = runner;
        _store = store;
        _matcher = matcher;
        _settlements = settlements;
        _transmissions = transmissions;
        _actor = actor;
        _options = options.Value;
    }

    [HttpPost("bai2")]
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

    /// <summary>Decisions; <c>status=Suspense</c> and <c>status=Return</c> are the work queue.</summary>
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
    public async Task<ActionResult<BankStatementSupersession>> ApproveSupersession(string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _supersessions.ApproveAsync(_actor.TenantId, id, _actor.UserId, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Takes a Requested supersession back, with a reason: the requester
    /// withdraws it, anyone else with this permission rejects it. The
    /// statement's lines can be worked again.
    /// </summary>
    [HttpPost("{id}/supersede/withdraw")]
    public async Task<ActionResult<BankStatementSupersession>> WithdrawSupersession(string id, [FromBody] SupersedeRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _supersessions.WithdrawOrRejectAsync(_actor.TenantId, id, request.Reason, _actor.UserId, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{id}/supersession")]
    public async Task<ActionResult<BankStatementSupersession>> GetSupersession(string id, CancellationToken ct) =>
        await _supersessions.GetAsync(_actor.TenantId, id, ct) is { } s ? Ok(s) : NotFound(new { error = $"No supersession for statement {id}" });

    [HttpGet("items")]
    public async Task<ActionResult<IReadOnlyList<ReconciliationItem>>> Items(
        [FromQuery] ReconciliationStatus? status, [FromQuery] string? statementId, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(await _store.ListItemsAsync(_actor.TenantId, _options.ServiceName, status, statementId, limit, ct));

    /// <summary>Whether the bank statement shows a NACHA file as settled.</summary>
    [HttpGet("settlements/{fileReference}")]
    public async Task<ActionResult<NachaFileSettlement>> Settlement(string fileReference, CancellationToken ct) =>
        await _settlements.GetAsync(_actor.TenantId, fileReference, ct) is { } s ? Ok(s) : NotFound(new { error = $"File {fileReference} is not settled" });

    [HttpPost("items/{id}/resolve")]
    public async Task<ActionResult<ReconciliationItem>> Resolve(string id, [FromBody] ResolvePaymentSuspenseRequest request, CancellationToken ct)
    {
        try
        {
            var item = await _store.GetItemAsync(_actor.TenantId, id, ct)
                       ?? throw new KeyNotFoundException($"Reconciliation item {id} not found");
            if (!item.IsOpen)
                return BadRequest(new { error = $"Reconciliation item {id} is {item.Status}, not open" });
            if (string.IsNullOrWhiteSpace(request.Note))
                return BadRequest(new { error = "A note saying why is required." });
            // Before anything is posted: a statement being replaced is worked from its corrected copy.
            await _runner.EnsureNotSupersededAsync(_actor.TenantId, item.StatementId, ct);

            switch (request.Action)
            {
                case PaymentSuspenseAction.MarkSettled:
                {
                    if (item.Status == ReconciliationStatus.Return)
                        return BadRequest(new { error = "A return does not settle a file." });
                    var file = string.IsNullOrWhiteSpace(request.FileReference) ? null : await _transmissions.GetAsync(_actor.TenantId, request.FileReference, ct);
                    if (file is null || file.Status != Models.PaymentFileTransmissionStatus.Transmitted)
                        return BadRequest(new { error = "fileReference must name a transmitted NACHA file." });
                    var tx = (await _store.GetTransactionsAsync(_actor.TenantId, item.StatementId, ct)).FirstOrDefault(t => t.Id == item.TransactionId)
                             ?? throw new KeyNotFoundException($"Bank transaction {item.TransactionId} not found");
                    var decision = await _matcher.SettleAsync(tx, file, _actor.UserId, ct);
                    if (decision.Status != ReconciliationStatus.Matched)
                        return BadRequest(new { error = decision.Reason });
                    return Ok(await _runner.ResolveAsync(_actor.TenantId, id, dismiss: false, request.Note, _actor.UserId,
                        "PaymentFileTransmission", [file.FileReference], ct));
                }
                case PaymentSuspenseAction.Resolve:
                    return Ok(await _runner.ResolveAsync(_actor.TenantId, id, dismiss: false, request.Note, _actor.UserId,
                        request.RelatedIds.Count > 0 ? "Payment" : null, request.RelatedIds, ct));
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

public static class PaymentBankReconciliationRegistration
{
    /// <summary>BAI2 ingestion, NACHA settlement and ACH return flags (docs/operations/BAI2-BANK-RECONCILIATION-RUNBOOK.md).</summary>
    public static IServiceCollection AddPaymentBankReconciliation(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, ChoDatabaseProvider? databaseProvider)
    {
        services.AddChoBankReconciliation(configuration, environment, "payment-service", databaseProvider);
        services.Configure<NachaSettlementOptions>(configuration.GetSection(NachaSettlementOptions.SectionName));
        if (databaseProvider == ChoDatabaseProvider.MongoDb)
        {
            services.AddScoped<INachaSettlementStore>(sp => new NachaSettlementStoreMongo(sp.GetRequiredService<IMongoDatabase>()));
            services.AddScoped<INachaSettlementCandidateSource>(sp => new NachaSettlementCandidateSourceMongo(sp.GetRequiredService<IMongoDatabase>()));
        }
        else
        {
            // The bank reconciliation store refuses without MongoDB, so nothing reaches these.
            services.AddSingleton<InMemoryNachaSettlementStore>();
            services.AddSingleton<INachaSettlementStore>(sp => sp.GetRequiredService<InMemoryNachaSettlementStore>());
            services.AddSingleton<INachaSettlementCandidateSource>(sp => sp.GetRequiredService<InMemoryNachaSettlementStore>());
        }
        services.AddScoped<NachaSettlementMatcher>();
        return services;
    }
}
