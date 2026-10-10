using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace PaymentService.Models;

/// <summary>
/// What a provider owes the plan, and how it was recovered: the provider
/// receivable ledger. A record is opened when a reversal run's 835 nets below
/// zero for a provider (BPR02 = 0 with a negative PLB FB carried forward) and is
/// worked down by later payment runs, which withhold it from that provider's
/// payments as a positive PLB (FB, or WO when configured) and reduce BPR02 and
/// the EFT credit by the same amount.
///
/// <para>
/// Append-only history: every change is a <see cref="ReceivableLedgerEntry"/>
/// (originated, recovered, recovery reversed) with who, when, which run and
/// which payment; <see cref="OutstandingAmount"/> is never written except
/// together with an entry, and never goes below zero
/// (<see cref="ApplyRecovery"/>). Writes are conditional on
/// <see cref="Version"/>, so two payment runs paying the same provider at the
/// same time cannot both recover the same dollars.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class ProviderReceivableRecord
{
    /// <summary>
    /// Deterministic per origin (<see cref="IdFor"/>), so recording the same
    /// forward balance twice (a retried reversal run step) is a no-op.
    /// </summary>
    [BsonId]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The payee NPI that owes it (the PLB01 provider of the origin 835).</summary>
    public string ProviderNpi { get; set; } = string.Empty;

    public string? TradingPartnerId { get; set; }

    /// <summary>Why it exists. Phase 1: <see cref="ReceivableOrigin.ReversalForwardBalance"/>.</summary>
    public ReceivableOrigin Origin { get; set; } = ReceivableOrigin.ReversalForwardBalance;

    public string? OriginRunId { get; set; }

    public string? OriginRunNumber { get; set; }

    /// <summary>The origin 835 (<see cref="EraEnvelopeRecord"/>) whose PLB FB carried the balance forward.</summary>
    public string? OriginEraEnvelopeId { get; set; }

    /// <summary>
    /// The origin 835's trace number (TRN02, also the reference of its PLB FB).
    /// A recovering PLB references it (PLB03-2), so the provider can tie the
    /// offset back to the 835 that created the balance.
    /// </summary>
    public string? OriginTraceNumber { get; set; }

    /// <summary>The PLB03-1 code a recovery is reported with: FB (default) or WO.</summary>
    public string RecoveryAdjustmentCode { get; set; } = "FB";

    /// <summary>The amount owed when the receivable was opened. Positive.</summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>What is still owed. Between 0 and <see cref="OriginalAmount"/>.</summary>
    public decimal OutstandingAmount { get; set; }

    public decimal RecoveredAmount { get; set; }

    public ReceivableStatus Status { get; set; } = ReceivableStatus.Open;

    /// <summary>
    /// While <see cref="ReceivableStatus.PendingVoid"/>: the original payments whose reversal
    /// opened this receivable and whose NACHA file had not been sent when the reversal ran.
    /// Nothing is recovered from the provider until each is either voided before payment
    /// (its part is cancelled) or found to have been paid (<see cref="Activate"/>).
    /// </summary>
    public List<string> PendingPaymentIds { get; set; } = new();

    /// <summary>When the balance arose (the origin 835): the start of aging.</summary>
    public DateTime OriginatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? LastRecoveredAt { get; set; }

    public DateTime? FullyRecoveredAt { get; set; }

    /// <summary>Optimistic concurrency: incremented by every write.</summary>
    public long Version { get; set; }

    public List<ReceivableLedgerEntry> Entries { get; set; } = new();

    public static string IdFor(ReceivableOrigin origin, string tenantId, string originKey)
        => $"rcv:{origin.ToString().ToLowerInvariant()}:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(originKey)}";

    /// <summary>Days since the balance arose, as of <paramref name="asOf"/> (0 when recovered later than that is irrelevant).</summary>
    public int AgeDays(DateTime asOf) => Math.Max(0, (int)(asOf.Date - OriginatedAt.Date).TotalDays);

    /// <summary>The aging bucket of the outstanding amount as of <paramref name="asOf"/>.</summary>
    public ReceivableAgingBucket AgingBucket(DateTime asOf) => ReceivableAging.BucketFor(AgeDays(asOf));

    /// <summary>
    /// Whether a payment has already recovered from this receivable (and the
    /// recovery was not reversed): a retried step must not recover twice.
    /// </summary>
    public bool HasLiveRecoveryFor(string paymentId)
    {
        var recovered = Entries.Count(e => e.Type == ReceivableEntryType.Recovered && e.PaymentId == paymentId);
        var reversed = Entries.Count(e => e.Type == ReceivableEntryType.RecoveryReversed && e.PaymentId == paymentId);
        return recovered > reversed;
    }

    /// <summary>
    /// Records a recovery of <paramref name="amount"/> against this receivable.
    /// Throws when the amount is not positive or is more than is outstanding:
    /// the receivable never goes negative.
    /// </summary>
    public ReceivableLedgerEntry ApplyRecovery(
        decimal amount, string? runId, string? runNumber, string? paymentId, string? traceNumber, string? by, DateTime at)
    {
        if (amount <= 0m)
            throw new InvalidOperationException($"A receivable recovery must be positive (was {amount:F2}).");
        if (Status == ReceivableStatus.PendingVoid)
            throw new InvalidOperationException(
                $"Receivable {Id} waits for the void of an unsent payment ({string.Join(", ", PendingPaymentIds)}); nothing is recovered from it yet.");
        if (amount > OutstandingAmount)
            throw new InvalidOperationException(
                $"Receivable {Id}: a recovery of {amount:F2} is more than the {OutstandingAmount:F2} outstanding; a receivable never goes negative.");

        OutstandingAmount -= amount;
        RecoveredAmount += amount;
        LastRecoveredAt = at;
        Status = OutstandingAmount == 0m ? ReceivableStatus.Recovered : ReceivableStatus.PartiallyRecovered;
        if (Status == ReceivableStatus.Recovered)
            FullyRecoveredAt = at;

        var entry = new ReceivableLedgerEntry
        {
            Type = ReceivableEntryType.Recovered,
            Amount = amount,
            OutstandingAfter = OutstandingAmount,
            RunId = runId,
            RunNumber = runNumber,
            PaymentId = paymentId,
            TraceNumber = traceNumber,
            At = at,
            By = by,
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Undoes a recovery whose payment was never issued (its insert failed after
    /// the ledger was written). Recorded as its own entry; nothing is deleted.
    /// </summary>
    public ReceivableLedgerEntry ReverseRecovery(string paymentId, string? by, DateTime at, string reason)
    {
        if (!HasLiveRecoveryFor(paymentId))
            throw new InvalidOperationException($"Receivable {Id} has no recovery by payment {paymentId} to reverse.");
        var amount = Entries.Last(e => e.Type == ReceivableEntryType.Recovered && e.PaymentId == paymentId).Amount;
        if (OutstandingAmount + amount > OriginalAmount)
            throw new InvalidOperationException($"Receivable {Id}: reversing {amount:F2} would exceed the original {OriginalAmount:F2}.");

        OutstandingAmount += amount;
        RecoveredAmount -= amount;
        FullyRecoveredAt = null;
        Status = RecoveredAmount == 0m ? ReceivableStatus.Open : ReceivableStatus.PartiallyRecovered;

        var entry = new ReceivableLedgerEntry
        {
            Type = ReceivableEntryType.RecoveryReversed,
            Amount = amount,
            OutstandingAfter = OutstandingAmount,
            PaymentId = paymentId,
            At = at,
            By = by,
            Note = reason,
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Whether this receivable was already reduced for the unpaid payment <paramref name="paymentId"/>.</summary>
    public bool HasCancellationFor(string paymentId)
        => Entries.Any(e => e.Type == ReceivableEntryType.CancelledUnpaid && e.PaymentId == paymentId);

    /// <summary>
    /// The payment whose reversal opened (part of) this receivable was never paid (voided
    /// before its NACHA file went out): nothing is owed back for it. Lowers what is
    /// outstanding by <paramref name="amount"/>; refused when less than that is outstanding
    /// (some of it was already recovered from the provider: that needs a person).
    /// </summary>
    public ReceivableLedgerEntry CancelForUnpaidPayment(string paymentId, decimal amount, string? by, DateTime at, string reason)
    {
        if (amount <= 0m)
            throw new InvalidOperationException($"A receivable cancellation must be positive (was {amount:F2}).");
        if (HasCancellationFor(paymentId))
            throw new InvalidOperationException($"Receivable {Id} was already reduced for payment {paymentId}.");
        if (amount > OutstandingAmount)
            throw new InvalidOperationException(
                $"Receivable {Id}: {amount:F2} cannot be cancelled, only {OutstandingAmount:F2} is outstanding (the rest was recovered from the provider); it needs a person.");

        OutstandingAmount -= amount;
        PendingPaymentIds.Remove(paymentId);
        if (OutstandingAmount == 0m)
        {
            Status = RecoveredAmount > 0m ? ReceivableStatus.Recovered : ReceivableStatus.Cancelled;
            FullyRecoveredAt ??= at;
            PendingPaymentIds.Clear();
        }
        else if (Status == ReceivableStatus.PendingVoid && PendingPaymentIds.Count == 0)
        {
            Status = RecoveredAmount > 0m ? ReceivableStatus.PartiallyRecovered : ReceivableStatus.Open;
        }
        var entry = new ReceivableLedgerEntry
        {
            Type = ReceivableEntryType.CancelledUnpaid,
            Amount = amount,
            OutstandingAfter = OutstandingAmount,
            PaymentId = paymentId,
            At = at,
            By = by,
            Note = reason,
        };
        Entries.Add(entry);
        return entry;
    }
}

/// <summary>Releasing a receivable held for the void of an unsent payment.</summary>
public static class ReceivablePending
{
    /// <summary>
    /// The pending payment <paramref name="paymentId"/> (or every pending payment, when null)
    /// was paid after all: its part is owed. Once none is pending, the receivable is Open
    /// and later payment runs recover it. Returns false when nothing changed.
    /// </summary>
    public static bool Activate(this ProviderReceivableRecord record, string? paymentId, string? by, DateTime at, string reason)
    {
        if (record.Status != ReceivableStatus.PendingVoid)
            return false;
        var released = paymentId == null ? record.PendingPaymentIds.ToList() : record.PendingPaymentIds.Where(p => p == paymentId).ToList();
        if (released.Count == 0)
            return false;
        foreach (var p in released)
            record.PendingPaymentIds.Remove(p);
        if (record.PendingPaymentIds.Count == 0)
            record.Status = record.OutstandingAmount == 0m ? ReceivableStatus.Cancelled : ReceivableStatus.Open;
        record.Entries.Add(new ReceivableLedgerEntry
        {
            Type = ReceivableEntryType.Activated,
            Amount = 0m,
            OutstandingAfter = record.OutstandingAmount,
            PaymentId = string.Join(",", released),
            At = at,
            By = by,
            Note = reason,
        });
        return true;
    }
}

/// <summary>One line of a receivable's history.</summary>
public class ReceivableLedgerEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");

    public ReceivableEntryType Type { get; set; }

    /// <summary>Positive (zero only for <see cref="ReceivableEntryType.Activated"/>); <see cref="Type"/> gives the direction.</summary>
    public decimal Amount { get; set; }

    /// <summary>The outstanding balance right after this entry.</summary>
    public decimal OutstandingAfter { get; set; }

    public string? RunId { get; set; }

    public string? RunNumber { get; set; }

    public string? PaymentId { get; set; }

    /// <summary>The 835 trace number (TRN02) the entry was reported under.</summary>
    public string? TraceNumber { get; set; }

    public DateTime At { get; set; }

    /// <summary>The token subject that released the run (the approver), never a request field.</summary>
    public string? By { get; set; }

    public string? Note { get; set; }
}

public enum ReceivableOrigin
{
    /// <summary>A reversal run's 835 netted below zero (PLB FB).</summary>
    ReversalForwardBalance = 0,
}

public enum ReceivableEntryType
{
    Originated = 0,
    Recovered = 1,
    RecoveryReversed = 2,

    /// <summary>The reversed payment was never paid (voided before transmission): nothing is owed for it.</summary>
    CancelledUnpaid = 3,

    /// <summary>A pending (unsent) payment was found to have been paid: the receivable may now be recovered.</summary>
    Activated = 4,
}

public enum ReceivableStatus
{
    Open = 0,
    PartiallyRecovered = 1,
    Recovered = 2,

    /// <summary>Nothing is owed any more: the reversed payments were never paid.</summary>
    Cancelled = 3,

    /// <summary>
    /// Opened by a reversal of payments whose NACHA file had not been sent: held, never
    /// recovered, until each such payment is voided before payment (its part is cancelled) or
    /// found to have been paid (then Open). See <see cref="ProviderReceivableRecord.PendingPaymentIds"/>.
    /// </summary>
    PendingVoid = 4,
}

public enum ReceivableAgingBucket
{
    Current0To30 = 0,
    Days31To60 = 1,
    Days61To90 = 2,
    Days91To120 = 3,
    Over120 = 4,
}

public static class ReceivableAging
{
    public static ReceivableAgingBucket BucketFor(int ageDays) => ageDays switch
    {
        <= 30 => ReceivableAgingBucket.Current0To30,
        <= 60 => ReceivableAgingBucket.Days31To60,
        <= 90 => ReceivableAgingBucket.Days61To90,
        <= 120 => ReceivableAgingBucket.Days91To120,
        _ => ReceivableAgingBucket.Over120,
    };
}

/// <summary>Outstanding receivables by aging bucket, per provider and in total.</summary>
public class ReceivableAgingReport
{
    public DateTime AsOf { get; set; }

    public decimal TotalOutstanding { get; set; }

    public Dictionary<ReceivableAgingBucket, decimal> Buckets { get; set; } = new();

    public List<ProviderReceivableAging> Providers { get; set; } = new();
}

public class ProviderReceivableAging
{
    public string ProviderNpi { get; set; } = string.Empty;

    public decimal TotalOutstanding { get; set; }

    public int OpenReceivables { get; set; }

    /// <summary>Age in days of the oldest outstanding receivable.</summary>
    public int OldestAgeDays { get; set; }

    public Dictionary<ReceivableAgingBucket, decimal> Buckets { get; set; } = new();
}
