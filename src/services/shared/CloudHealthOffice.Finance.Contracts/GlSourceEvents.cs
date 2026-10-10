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

    // Seams, not produced yet:
    //   PaymentFileSettled   - bank acknowledgement / settlement: ACH in transit -> cash.
    //   PaymentFileReturned  - an ACH return: cash/in transit -> payable again.
    //   CapitationDisbursed, PremiumCashReceived - capitation-service / premium-billing-service.

    public static readonly IReadOnlyList<string> All = [PaymentRunExecuted, ReversalRunExecuted, PaymentFileTransmitted];

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
}
