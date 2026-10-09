using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// The provider receivable ledger: opens a receivable when a reversal 835
/// carries a negative balance forward (PLB FB), and recovers it from the
/// provider's later payments as a positive PLB offset (FB by default, WO when
/// <c>Receivables:RecoveryAdjustmentCode</c> says so), reducing the payment's
/// BPR02 and EFT amount by the same amount.
///
/// <para>
/// Order of writes in a payment run: the ledger is written first (one
/// conditional write per receivable, so concurrent runs paying the same
/// provider cannot both take the same dollars), then the payment carrying the
/// matching PLB is inserted. If that insert fails, the recovery is reversed
/// with its own ledger entry. A crash between the two leaves a Recovered entry
/// naming a payment id that does not exist; that is visible in the ledger (the
/// entry carries the run and payment id) and is the one case that needs a
/// person.
/// </para>
/// </summary>
public interface IProviderReceivableLedger
{
    /// <summary>
    /// Opens (idempotently, keyed by the origin 835) a receivable for a
    /// reversal 835 that carried <paramref name="amountOwed"/> forward.
    /// </summary>
    Task<ProviderReceivableRecord> RecordForwardBalanceAsync(
        string tenantId, string providerNpi, string? tradingPartnerId, ReversalRun run,
        string eraEnvelopeId, string? traceNumber, decimal amountOwed, string? recordedBy);

    /// <summary>
    /// Recovers what the payment's payee owes, oldest receivable first, up to
    /// the payment's amount. Adds one PLB per receivable touched to
    /// <paramref name="payment"/>, records it in <see cref="Payment.ReceivableOffsets"/>,
    /// and lowers <see cref="Payment.TotalPaymentAmount"/> (never below zero).
    /// The ledger is written before this returns. Call before the payment is inserted.
    /// All or nothing: when it throws, every recovery it made has been reversed
    /// (or logged as needing a person) and the payment is as it was.
    /// </summary>
    Task<IReadOnlyList<ReceivableOffset>> ApplyRecoveriesAsync(
        string tenantId, Payment payment, string runId, string runNumber, string? recoveredBy);

    /// <summary>
    /// Reverses the recoveries a payment made when the payment was never issued.
    /// Throws (after trying every receivable) when any could not be reversed.
    /// </summary>
    Task ReverseRecoveriesAsync(string tenantId, Payment payment, string? by, string reason);

    Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null);

    Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id);

    Task<ReceivableAgingReport> GetAgingAsync(string tenantId, DateTime asOf, string? providerNpi = null);
}

public sealed class ProviderReceivableLedger : IProviderReceivableLedger
{
    public const string ConfigurationSection = "Receivables";
    private const int MaxConflictRetries = 5;

    private readonly IProviderReceivableRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderReceivableLedger> _logger;

    public ProviderReceivableLedger(
        IProviderReceivableRepository repository,
        IConfiguration configuration,
        ILogger<ProviderReceivableLedger> logger,
        TimeProvider? time = null)
    {
        _repository = repository;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The PLB03-1 code a recovery is reported with: FB (the default; the
    /// positive counterpart of the origin 835's negative FB) or WO (overpayment
    /// recovery). Anything else is refused at use.
    /// </summary>
    private string RecoveryCode()
    {
        var code = (_configuration[$"{ConfigurationSection}:RecoveryAdjustmentCode"] ?? "FB").Trim().ToUpperInvariant();
        return code is "FB" or "WO"
            ? code
            : throw new InvalidOperationException(
                $"{ConfigurationSection}:RecoveryAdjustmentCode must be FB or WO (was '{code}').");
    }

    public async Task<ProviderReceivableRecord> RecordForwardBalanceAsync(
        string tenantId, string providerNpi, string? tradingPartnerId, ReversalRun run,
        string eraEnvelopeId, string? traceNumber, decimal amountOwed, string? recordedBy)
    {
        if (amountOwed <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amountOwed), "A receivable is opened for a positive amount owed.");
        if (string.IsNullOrWhiteSpace(providerNpi))
            throw new InvalidOperationException($"Reversal 835 {eraEnvelopeId} names no payee NPI; its receivable cannot be attributed.");

        var now = _time.GetUtcNow().UtcDateTime;
        var record = new ProviderReceivableRecord
        {
            // One receivable per provider per origin 835 (an 835 can carry
            // several providers' forward balances, each its own PLB FB).
            Id = ProviderReceivableRecord.IdFor(ReceivableOrigin.ReversalForwardBalance, tenantId, $"{eraEnvelopeId}|{providerNpi}"),
            TenantId = tenantId,
            ProviderNpi = providerNpi,
            TradingPartnerId = tradingPartnerId,
            Origin = ReceivableOrigin.ReversalForwardBalance,
            OriginRunId = run.Id,
            OriginRunNumber = run.ReversalRunNumber,
            OriginEraEnvelopeId = eraEnvelopeId,
            OriginTraceNumber = traceNumber,
            RecoveryAdjustmentCode = RecoveryCode(),
            OriginalAmount = amountOwed,
            OutstandingAmount = amountOwed,
            RecoveredAmount = 0m,
            Status = ReceivableStatus.Open,
            OriginatedAt = now,
            CreatedBy = recordedBy,
            Version = 1,
            Entries =
            {
                new ReceivableLedgerEntry
                {
                    Type = ReceivableEntryType.Originated,
                    Amount = amountOwed,
                    OutstandingAfter = amountOwed,
                    RunId = run.Id,
                    RunNumber = run.ReversalRunNumber,
                    TraceNumber = traceNumber,
                    At = now,
                    By = recordedBy,
                    Note = $"Reversal 835 {eraEnvelopeId} carried a negative balance forward (PLB FB)",
                },
            },
        };

        var stored = await _repository.CreateIfAbsentAsync(record);
        _logger.LogInformation(
            "AUDIT provider receivable {ReceivableId} opened for NPI {Npi} in tenant {TenantId}: {Amount:F2} from reversal run {RunNumber} (835 {EnvelopeId}) by {By}",
            stored.Id, Sanitize(providerNpi), Sanitize(tenantId), stored.OriginalAmount, run.ReversalRunNumber, Sanitize(eraEnvelopeId), Sanitize(recordedBy));
        return stored;
    }

    public async Task<IReadOnlyList<ReceivableOffset>> ApplyRecoveriesAsync(
        string tenantId, Payment payment, string runId, string runNumber, string? recoveredBy)
    {
        var offsets = new List<ReceivableOffset>();
        if (payment.IsReversal || string.IsNullOrEmpty(payment.PayeeNPI) || payment.TotalPaymentAmount <= 0m)
            return offsets;

        // All or nothing: if any receivable fails part way, every recovery this
        // payment already made is reversed and the payment is left as it was,
        // so no offset is ever recorded against a payment that is not issued.
        var originalAmount = payment.TotalPaymentAmount;
        var originalOffsets = payment.ReceivableOffsets.Count;
        var originalAdjustments = payment.ProviderAdjustments.Count;
        var touched = new List<string>();
        try
        {
            var outstanding = await _repository.ListOutstandingAsync(tenantId, payment.PayeeNPI);
            foreach (var candidate in outstanding)
            {
                if (payment.TotalPaymentAmount <= 0m)
                    break;

                // Touched before the write: a write that landed although it
                // reported a failure is reversed too.
                touched.Add(candidate.Id);
                var applied = await ApplyOneAsync(tenantId, candidate.Id, payment, runId, runNumber, recoveredBy);
                if (applied == null)
                    continue;

                offsets.Add(applied);
                payment.ReceivableOffsets.Add(applied);
                payment.ProviderAdjustments.Add(new ProviderAdjustment
                {
                    AdjustmentIdentifier = applied.AdjustmentCode,
                    ReferenceIdentification = applied.Reference,
                    Amount = applied.Amount,
                    FiscalPeriodEnd = payment.PaymentDate,
                    Description = $"Recovery of provider receivable {applied.ReceivableId}",
                    // PLB01: the payee this payment pays, even in a multi-payee 835.
                    ProviderIdentifier = payment.PayeeNPI,
                });
                payment.TotalPaymentAmount -= applied.Amount;
            }

            return offsets;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Receivable recovery for payment {PaymentId} (run {RunNumber}) failed part way; reversing the {Count} recoveries it made",
                payment.Id, runNumber, offsets.Count);
            var unreversed = await ReverseForPaymentAsync(tenantId, payment.Id, touched, recoveredBy,
                $"recovery for payment {payment.Id} of run {runNumber} failed part way ({ex.GetType().Name})");
            if (unreversed.Count > 0)
                _logger.LogCritical(
                    "Receivables {ReceivableIds} keep a recovery by payment {PaymentId}, which is not issued; they need a person",
                    string.Join(", ", unreversed), payment.Id);

            payment.TotalPaymentAmount = originalAmount;
            payment.ReceivableOffsets.RemoveRange(originalOffsets, payment.ReceivableOffsets.Count - originalOffsets);
            payment.ProviderAdjustments.RemoveRange(originalAdjustments, payment.ProviderAdjustments.Count - originalAdjustments);
            throw;
        }
    }

    private async Task<ReceivableOffset?> ApplyOneAsync(
        string tenantId, string receivableId, Payment payment, string runId, string runNumber, string? by)
    {
        for (var attempt = 0; attempt < MaxConflictRetries; attempt++)
        {
            var current = await _repository.GetAsync(tenantId, receivableId);
            if (current == null)
                return null;

            // Before the "nothing outstanding" check: a retried step whose
            // recovery closed the receivable must still get its offset back.
            if (current.HasLiveRecoveryFor(payment.Id))
            {
                var amountAlready = current.Entries.Last(e => e.Type == ReceivableEntryType.Recovered && e.PaymentId == payment.Id).Amount;
                return new ReceivableOffset
                {
                    ReceivableId = current.Id,
                    Amount = amountAlready,
                    AdjustmentCode = current.RecoveryAdjustmentCode,
                    Reference = current.OriginTraceNumber,
                };
            }

            if (current.OutstandingAmount <= 0m)
                return null;

            var amount = Math.Min(current.OutstandingAmount, payment.TotalPaymentAmount);
            if (amount <= 0m)
                return null;

            var expected = current.Version;
            current.ApplyRecovery(amount, runId, runNumber, payment.Id, payment.CheckNumber, by, _time.GetUtcNow().UtcDateTime);
            current.Version = expected + 1;
            if (await _repository.TryReplaceAsync(current, expected))
            {
                _logger.LogInformation(
                    "AUDIT provider receivable {ReceivableId} (NPI {Npi}): {Amount:F2} recovered by payment {PaymentId} (trace {Trace}) in payment run {RunNumber} by {By}; {Outstanding:F2} outstanding",
                    current.Id, Sanitize(current.ProviderNpi), amount, payment.Id, Sanitize(payment.CheckNumber), runNumber, Sanitize(by), current.OutstandingAmount);
                return new ReceivableOffset
                {
                    ReceivableId = current.Id,
                    Amount = amount,
                    AdjustmentCode = current.RecoveryAdjustmentCode,
                    Reference = current.OriginTraceNumber,
                };
            }
        }

        throw new InvalidOperationException(
            $"Provider receivable {receivableId} kept changing while payment run {runNumber} tried to recover it; nothing was recovered from payment {payment.Id}.");
    }

    public async Task ReverseRecoveriesAsync(string tenantId, Payment payment, string? by, string reason)
    {
        var unreversed = await ReverseForPaymentAsync(
            tenantId, payment.Id, payment.ReceivableOffsets.Select(o => o.ReceivableId).ToList(), by, reason);
        if (unreversed.Count > 0)
            throw new InvalidOperationException(
                $"Recoveries by payment {payment.Id} could not be reversed on receivables {string.Join(", ", unreversed)}; they need a person.");
    }

    /// <summary>
    /// Reverses the live recovery <paramref name="paymentId"/> holds on each
    /// receivable (none is a no-op). Returns the receivables where it could not
    /// (conflicts kept coming, or the store failed); each is logged as an error.
    /// </summary>
    private async Task<List<string>> ReverseForPaymentAsync(
        string tenantId, string paymentId, IReadOnlyCollection<string> receivableIds, string? by, string reason)
    {
        var unreversed = new List<string>();
        foreach (var receivableId in receivableIds.Distinct(StringComparer.Ordinal))
        {
            var done = false;
            try
            {
                for (var attempt = 0; attempt < MaxConflictRetries && !done; attempt++)
                {
                    var current = await _repository.GetAsync(tenantId, receivableId);
                    if (current == null || !current.HasLiveRecoveryFor(paymentId))
                    {
                        done = true;
                        break;
                    }

                    var expected = current.Version;
                    current.ReverseRecovery(paymentId, by, _time.GetUtcNow().UtcDateTime, reason);
                    current.Version = expected + 1;
                    if (await _repository.TryReplaceAsync(current, expected))
                    {
                        _logger.LogWarning(
                            "AUDIT provider receivable {ReceivableId}: recovery by payment {PaymentId} reversed ({Reason}); {Outstanding:F2} outstanding",
                            current.Id, paymentId, Sanitize(reason), current.OutstandingAmount);
                        done = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reversing the recovery by payment {PaymentId} on receivable {ReceivableId} failed", paymentId, receivableId);
            }

            if (!done)
            {
                _logger.LogError(
                    "The recovery by payment {PaymentId} on receivable {ReceivableId} could not be reversed; it needs a person",
                    paymentId, receivableId);
                unreversed.Add(receivableId);
            }
        }
        return unreversed;
    }

    public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null)
        => _repository.SearchAsync(tenantId, providerNpi, status);

    public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id) => _repository.GetAsync(tenantId, id);

    public async Task<ReceivableAgingReport> GetAgingAsync(string tenantId, DateTime asOf, string? providerNpi = null)
    {
        var records = (await _repository.SearchAsync(tenantId, providerNpi))
            .Where(r => r.OutstandingAmount > 0m)
            .ToList();

        var report = new ReceivableAgingReport { AsOf = asOf };
        foreach (var bucket in Enum.GetValues<ReceivableAgingBucket>())
            report.Buckets[bucket] = 0m;

        foreach (var group in records.GroupBy(r => r.ProviderNpi, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var provider = new ProviderReceivableAging
            {
                ProviderNpi = group.Key,
                OpenReceivables = group.Count(),
                OldestAgeDays = group.Max(r => r.AgeDays(asOf)),
            };
            foreach (var bucket in Enum.GetValues<ReceivableAgingBucket>())
                provider.Buckets[bucket] = 0m;
            foreach (var r in group)
            {
                var bucket = r.AgingBucket(asOf);
                provider.Buckets[bucket] += r.OutstandingAmount;
                provider.TotalOutstanding += r.OutstandingAmount;
                report.Buckets[bucket] += r.OutstandingAmount;
                report.TotalOutstanding += r.OutstandingAmount;
            }
            report.Providers.Add(provider);
        }

        return report;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
