using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudHealthOffice.Finance.Contracts;

/// <summary>
/// Event types a general ledger posts from. Each is published at least once (outbox
/// plus dispatcher); the consumer de-duplicates on <see cref="GlEventEnvelope.EventId"/>
/// and, more strongly, on the business key of the entry it produces.
/// </summary>
public static class GlEventTypes
{
    /// <summary>A payment run issued payments: claims expense and claims payable accrue.</summary>
    public const string PaymentRunExecuted = "PaymentRunExecuted";

    /// <summary>A reversal run recouped earlier payments: the expense reverses into a provider receivable.</summary>
    public const string ReversalRunExecuted = "ReversalRunExecuted";

    /// <summary>A payment run's NACHA file is at the bank: the payable moves to ACH in transit.</summary>
    public const string PaymentFileTransmitted = "PaymentFileTransmitted";

    /// <summary>
    /// A payment was reversed before its NACHA file was sent and was taken out of the file:
    /// it will never be paid. Its payable is relieved against the receivable the reversal opened.
    /// </summary>
    public const string PaymentVoidedBeforeTransmission = "PaymentVoidedBeforeTransmission";

    /// <summary>
    /// A provider is owed a refund: an amount was withheld from one of its payments against a
    /// receivable for a payment that was never made. Not posted automatically: an open item
    /// until finance posts the refund by hand and dismisses it.
    /// </summary>
    public const string ProviderRefundOwed = "ProviderRefundOwed";

    /// <summary>
    /// The unreversed remainder of a payment excluded from its file was paid to the provider
    /// another way (e.g. a manual check): the remaining payable moves to cash.
    /// </summary>
    public const string VoidRemainderReissued = "VoidRemainderReissued";

    /// <summary>A bank debit settled a whole credits-only claims NACHA file.</summary>
    public const string PaymentFileSettled = "PaymentFileSettled";

    // Seams, not produced yet:
    //   PaymentFileReturned  - an ACH return: cash/in transit -> payable again.
    //   CapitationDisbursed, PremiumCashReceived - capitation-service / premium-billing-service.

    public static readonly IReadOnlyList<string> All = [PaymentRunExecuted, ReversalRunExecuted, PaymentFileTransmitted, PaymentVoidedBeforeTransmission, ProviderRefundOwed, VoidRemainderReissued, PaymentFileSettled];

    /// <summary>The JSON options every GL payload is written and read with (camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A stable event id for (tenant, source, type): a redelivered or re-emitted event keeps its id.</summary>
    public static string EventIdFor(string tenantId, string sourceKey, string type)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{sourceKey}\n{type}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

/// <summary>What the dispatcher delivers: the event's identity and its payload, verbatim.</summary>
public sealed record GlEventEnvelope
{
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;

    /// <summary>The service that produced it (payment-service).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>camelCase JSON of the typed payload.</summary>
    public string PayloadJson { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

/// <summary>One issued payment of a run. Amounts are money (2 decimals), never negative.</summary>
public sealed record GlPaymentLine
{
    public string PaymentId { get; init; } = string.Empty;

    /// <summary>The check / EFT trace number (835 TRN02).</summary>
    public string CheckNumber { get; init; } = string.Empty;
    public string? PayeeNpi { get; init; }

    /// <summary>ACH or CHK, as issued.</summary>
    public string PaymentMethod { get; init; } = string.Empty;

    /// <summary>What the payee is paid (835 BPR02 share): claim payments less receivable offsets.</summary>
    public decimal NetAmount { get; init; }

    /// <summary>Provider receivables recovered from this payment (PLB FB/WO); gross = net + offsets.</summary>
    public decimal ReceivableOffsetAmount { get; init; }
}

/// <summary>A payment run issued payments.</summary>
public sealed record PaymentRunExecutedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;

    /// <summary>Completed, or Failed after some payments were already issued (they are liabilities all the same).</summary>
    public string RunStatus { get; init; } = string.Empty;
    public string? LineOfBusiness { get; init; }

    /// <summary>The payment date (835 BPR16 = NACHA effective entry date).</summary>
    public DateTime PaymentDate { get; init; }
    public DateTime ExecutedAt { get; init; }
    public string ExecutedBy { get; init; } = string.Empty;
    public List<GlPaymentLine> Payments { get; init; } = new();
    public decimal TotalNetAmount { get; init; }
    public decimal TotalReceivableOffsetAmount { get; init; }
}

/// <summary>One recouped payment of a reversal run (the reversal payment's amount, made positive).</summary>
public sealed record GlReversalLine
{
    public string ReversalPaymentId { get; init; } = string.Empty;
    public string CheckNumber { get; init; } = string.Empty;
    public string? PayeeNpi { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>A reversal run recouped earlier payments (now owed back by the providers).</summary>
public sealed record ReversalRunExecutedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string ReversalRunId { get; init; } = string.Empty;
    public string ReversalRunNumber { get; init; } = string.Empty;
    public string RunStatus { get; init; } = string.Empty;
    public DateTime ExecutedAt { get; init; }
    public string ExecutedBy { get; init; } = string.Empty;
    public List<GlReversalLine> Reversals { get; init; } = new();
    public decimal TotalAmount { get; init; }
}

/// <summary>
/// The NACHA file of a payment run is at the bank: cash leaves the operating account
/// on <see cref="EffectiveEntryDate"/> for <see cref="TotalCreditAmount"/>. Totals and
/// identifiers only, no bank numbers.
/// </summary>
public sealed record PaymentFileTransmittedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;
    public string FileReference { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int EntryCount { get; init; }
    public decimal TotalCreditAmount { get; init; }
    public decimal TotalDebitAmount { get; init; }
    public DateTime EffectiveEntryDate { get; init; }
    public DateTime TransmittedAt { get; init; }
    public string ApprovedBy { get; init; } = string.Empty;
    public string ConfirmedBy { get; init; } = string.Empty;

    /// <summary>
    /// The payments the file pays (sorted). Payments of the run not listed here are not in
    /// the file: check fallbacks decided after execution, zero amounts, payments voided before
    /// transmission. Empty on events written before this field existed.
    /// </summary>
    public List<string> PaymentIds { get; init; } = new();

    /// <summary>When the run was executed (its accrual's date): decides the GL cutover. Null on older events.</summary>
    public DateTime? PaymentRunExecutedAt { get; init; }
}

/// <summary>
/// A payment of a run was reversed before the run's NACHA file went to the bank, and a
/// re-pinned file leaves it out: it is never paid. Its 835 already told the provider it
/// would be; the provider must be told it will not.
/// </summary>
public sealed record PaymentVoidedBeforeTransmissionEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string CheckNumber { get; init; } = string.Empty;
    public string? PayeeNpi { get; init; }

    /// <summary>
    /// The payable now never to be paid: what the reversal recouped of the payment, net of
    /// receivables the payment itself recovered. Normally the payment's net amount.
    /// </summary>
    public decimal Amount { get; init; }

    /// <summary>The part of the payment that was not reversed: still payable (to be reissued). Informational.</summary>
    public decimal UnpaidRemainder { get; init; }

    /// <summary>The reversal run(s) whose recoupment opened the receivable this void cancels (comma separated when split).</summary>
    public string? ReversalRunId { get; init; }

    /// <summary>When the (latest) recouping reversal run executed: the void follows its recoupment across the GL cutover.</summary>
    public DateTime? ReversalRunExecutedAt { get; init; }

    /// <summary>Excluded from the file by a finance decision rather than the automatic re-pin.</summary>
    public bool ExcludedByOverride { get; init; }
    public string SupersededFileReference { get; init; } = string.Empty;
    public string NewFileReference { get; init; } = string.Empty;
    public DateTime VoidedAt { get; init; }
    public string VoidedBy { get; init; } = string.Empty;

    /// <summary>When the payment's run was executed (its accrual's date): decides the GL cutover.</summary>
    public DateTime? PaymentRunExecutedAt { get; init; }
}

/// <summary>
/// A recovery was reversed because its receivable reverses a payment that was never made
/// (voided before transmission): the provider is owed <see cref="Amount"/> back. An open item
/// in the GL reconciliation until finance posts the refund by hand and dismisses the event.
/// </summary>
public sealed record ProviderRefundOwedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;
    public string VoidedPaymentId { get; init; } = string.Empty;
    public string VoidedCheckNumber { get; init; } = string.Empty;
    public string? ProviderNpi { get; init; }
    public string ReceivableId { get; init; } = string.Empty;

    /// <summary>The later payment the amount was withheld from (a PLB offset), and its run.</summary>
    public string RecoveringPaymentId { get; init; } = string.Empty;
    public string? RecoveringRunNumber { get; init; }
    public decimal Amount { get; init; }
    public DateTime RecordedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
}

/// <summary>The unreversed remainder of an excluded payment was paid to the provider another way.</summary>
public sealed record VoidRemainderReissuedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string PaymentRunNumber { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string CheckNumber { get; init; } = string.Empty;
    public string? PayeeNpi { get; init; }

    /// <summary>The remainder paid: what was still payable for the excluded payment.</summary>
    public decimal Amount { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime ReissuedAt { get; init; }
    public string ReissuedBy { get; init; } = string.Empty;
    public DateTime? PaymentRunExecutedAt { get; init; }
}

/// <summary>Bank-confirmed settlement, no bank account or member details.</summary>
public sealed record PaymentFileSettledEvent
{
    public string EventId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string PaymentRunId { get; init; } = string.Empty;
    public string FileReference { get; init; } = string.Empty;
    public string TransmissionEventId { get; init; } = string.Empty;
    public string BankTransactionId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTime BankAsOfDate { get; init; }
    public DateTime? PaymentRunExecutedAt { get; init; }
    public string SettledBy { get; init; } = string.Empty;
}
