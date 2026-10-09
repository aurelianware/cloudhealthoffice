using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace PremiumBillingService.Models;

/// <summary>Where a remittance came from.</summary>
public enum RemittanceSource
{
    /// <summary>X12 005010X218 820 Payroll Deducted and Other Group Premium Payment.</summary>
    X12820,

    /// <summary>Bank lockbox file (CSV).</summary>
    Lockbox
}

/// <summary>Whether the remittance detail is for the whole group or one member.</summary>
public enum RemittanceLevel
{
    /// <summary>820 loop 2000A (organization summary remittance), or a lockbox row.</summary>
    Organization,

    /// <summary>820 loop 2000B (individual remittance).</summary>
    Individual
}

/// <summary>
/// One payment with its remittance detail, normalized from an 820 transaction
/// set or a lockbox file. Both go through the same cash application.
/// </summary>
public class RemittanceAdvice
{
    public RemittanceSource Source { get; set; }

    public string? SourceFileName { get; set; }

    /// <summary>820 TRN02 / lockbox batch + item: the payment's trace number.</summary>
    public string TraceNumber { get; set; } = string.Empty;

    /// <summary>820 TRN03 or N1*PR identifier / lockbox payer account.</summary>
    public string PayerId { get; set; } = string.Empty;

    public string? PayerName { get; set; }

    /// <summary>820 BPR01 transaction handling code (C, D, I, P, U, X); null for lockbox.</summary>
    public string? TransactionHandlingCode { get; set; }

    /// <summary>820 BPR04 (ACH, CHK, FWT, BOP, NON) / "CHK" for lockbox.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>820 BPR02 / lockbox check amount: the money received.</summary>
    public decimal PaymentAmount { get; set; }

    /// <summary>820 BPR16 / lockbox deposit date.</summary>
    public DateTime PaymentDate { get; set; }

    /// <summary>Check number when paid by check.</summary>
    public string? CheckNumber { get; set; }

    public List<RemittanceItem> Items { get; set; } = new();

    /// <summary>Parser notes (e.g. SE segment count mismatch); carried onto the batch.</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>One RMR (or lockbox row): an amount paid against a reference.</summary>
public class RemittanceItem
{
    /// <summary>1-based position of the item in the payment.</summary>
    public int LineNumber { get; set; }

    public RemittanceLevel Level { get; set; }

    /// <summary>820 ENT01 assigned number of the entity loop the item is in.</summary>
    public string? EntityNumber { get; set; }

    /// <summary>820 ENT02/ENT03/ENT04 (e.g. 2J EI 123456789): who the remittance is for.</summary>
    public string? EntityIdQualifier { get; set; }

    public string? EntityId { get; set; }

    /// <summary>820 NM1*IL member identifier (NM109) at the individual level.</summary>
    public string? MemberId { get; set; }

    public string? MemberName { get; set; }

    /// <summary>820 RMR01 reference qualifier (e.g. IK, 11, AZ).</summary>
    public string? ReferenceQualifier { get; set; }

    /// <summary>820 RMR02 / lockbox invoice number: matched to the invoice number.</summary>
    public string? Reference { get; set; }

    /// <summary>820 RMR04: amount paid.</summary>
    public decimal Amount { get; set; }

    /// <summary>820 RMR05: amount billed, as the payer saw it.</summary>
    public decimal? BilledAmount { get; set; }

    /// <summary>820 DTM*582 coverage period, when given.</summary>
    public DateTime? CoveragePeriodStart { get; set; }

    public DateTime? CoveragePeriodEnd { get; set; }

    /// <summary>820 ADX adjustments reported with the item (amount, reason code).</summary>
    public List<RemittanceAdjustmentDetail> Adjustments { get; set; } = new();
}

public class RemittanceAdjustmentDetail
{
    public decimal Amount { get; set; }
    public string? ReasonCode { get; set; }
}

public enum RemittanceBatchStatus
{
    /// <summary>Recorded; items are being applied.</summary>
    Processing,

    /// <summary>Every item was applied or routed to the exceptions queue.</summary>
    Completed,

    /// <summary>
    /// Recorded but no cash was posted: the 820 moves no money
    /// (BPR01 I or P) or its detail exceeds the payment.
    /// </summary>
    NotPosted
}

public enum CashApplicationOutcome
{
    /// <summary>Paid exactly the invoice balance.</summary>
    ExactMatch,

    /// <summary>Paid less than the invoice balance.</summary>
    PartialPayment,

    /// <summary>Paid more than the balance: the balance is paid and the rest is unapplied credit.</summary>
    Overpayment,

    /// <summary>Not applied: see the exceptions queue.</summary>
    Exception
}

/// <summary>
/// A received payment and what was done with each of its items. The id is
/// derived from tenant, source, payer and trace number, so a payment can be
/// recorded once only: a second upload of the same 820 or lockbox item is
/// refused, not posted twice.
/// </summary>
public class RemittanceBatch
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public RemittanceSource Source { get; set; }
    public string? SourceFileName { get; set; }
    public string TraceNumber { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string? PayerName { get; set; }
    public string? TransactionHandlingCode { get; set; }
    public string? PaymentMethod { get; set; }
    public string? CheckNumber { get; set; }
    public decimal PaymentAmount { get; set; }
    public DateTime PaymentDate { get; set; }

    public RemittanceBatchStatus Status { get; set; } = RemittanceBatchStatus.Processing;

    public List<RemittanceApplication> Applications { get; set; } = new();

    /// <summary>Cash applied to invoices.</summary>
    public decimal AppliedAmount { get; set; }

    /// <summary>Overpayment held as unapplied credit on sponsor accounts.</summary>
    public decimal UnappliedCreditAmount { get; set; }

    /// <summary>Cash sent to the exceptions queue.</summary>
    public decimal ExceptionAmount { get; set; }

    public int ExceptionCount { get; set; }

    public List<string> Warnings { get; set; } = new();

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [StringLength(200)]
    public string? ProcessedBy { get; set; }

    /// <summary>The batch id for a payment: one per tenant, source, payer and trace number.</summary>
    public static string IdFor(string tenantId, RemittanceSource source, string payerId, string traceNumber)
    {
        var key = $"{tenantId}|{source}|{payerId.Trim().ToUpperInvariant()}|{traceNumber.Trim().ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"rmt-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}";
    }
}

/// <summary>What happened to one remittance item.</summary>
public class RemittanceApplication
{
    public int LineNumber { get; set; }
    public RemittanceLevel Level { get; set; }
    public string? Reference { get; set; }
    public string? MemberId { get; set; }
    public decimal PaidAmount { get; set; }
    public CashApplicationOutcome Outcome { get; set; }
    public string? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? GroupNumber { get; set; }

    /// <summary>Invoice balance before this item.</summary>
    public decimal? BalanceBefore { get; set; }

    public decimal AppliedAmount { get; set; }
    public decimal UnappliedCreditAmount { get; set; }
    public string? PaymentId { get; set; }
    public string? ExceptionId { get; set; }
}

public enum RemittanceExceptionReason
{
    /// <summary>The item has no reference to match on.</summary>
    MissingReference,

    /// <summary>No invoice has the referenced number.</summary>
    InvoiceNotFound,

    /// <summary>The referenced invoice is voided or written off.</summary>
    InvoiceClosed,

    /// <summary>More than one open invoice has the referenced number.</summary>
    AmbiguousReference,

    /// <summary>Zero or negative amount (a reversal or recoupment needs a person).</summary>
    NonPositiveAmount,

    /// <summary>Money received that no item accounts for.</summary>
    UnallocatedRemainder,

    /// <summary>The items add up to more than the money received; nothing in the payment was posted.</summary>
    DetailExceedsPayment
}

public enum RemittanceExceptionStatus
{
    Open,

    /// <summary>A person applied it to an invoice.</summary>
    Applied,

    /// <summary>A person put it on a sponsor account as unapplied credit.</summary>
    Credited,

    /// <summary>A person closed it without posting (e.g. refunded or returned).</summary>
    Dismissed
}

/// <summary>Cash that could not be applied automatically, waiting for a person.</summary>
public class RemittanceException
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string BatchId { get; set; } = string.Empty;
    public RemittanceSource Source { get; set; }
    public string TraceNumber { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string? PayerName { get; set; }
    public DateTime PaymentDate { get; set; }

    /// <summary>Item line number; 0 for a payment-level exception (remainder, out of balance).</summary>
    public int LineNumber { get; set; }

    public string? Reference { get; set; }
    public string? MemberId { get; set; }
    public decimal Amount { get; set; }

    public RemittanceExceptionReason Reason { get; set; }

    [StringLength(1000)]
    public string? Detail { get; set; }

    public RemittanceExceptionStatus Status { get; set; } = RemittanceExceptionStatus.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    [StringLength(200)]
    public string? ResolvedBy { get; set; }

    [StringLength(1000)]
    public string? ResolutionNote { get; set; }

    public string? AppliedInvoiceId { get; set; }
    public string? CreditedGroupNumber { get; set; }
}

/// <summary>
/// A sponsor group's receivable position: what its open invoices owe and the
/// unapplied credit (overpayments) it holds. Updated by every cash posting.
/// </summary>
public class SponsorAccount
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>Sum of the balances due on the group's open (not voided or written-off) invoices.</summary>
    public decimal OpenInvoiceBalance { get; set; }

    /// <summary>Overpayments received and not yet applied to an invoice.</summary>
    public decimal UnappliedCredit { get; set; }

    /// <summary>What the group owes after its credit (negative: the group is in credit).</summary>
    public decimal NetBalance { get; set; }

    public DateTime? LastPaymentAt { get; set; }

    public List<SponsorAccountEntry> Entries { get; set; } = new();

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optimistic concurrency (Mongo); incremented on every write.</summary>
    public long Version { get; set; }

    /// <summary>Optimistic concurrency (Cosmos); set by Cosmos DB.</summary>
    [JsonPropertyName("_etag")]
    [BsonIgnore]
    public string? ETag { get; set; }

    public static string IdFor(string tenantId, string groupNumber) => $"acct-{tenantId}-{groupNumber.Trim().ToUpperInvariant()}";
}

public enum SponsorAccountEntryType
{
    /// <summary>An overpayment held as unapplied credit.</summary>
    OverpaymentCredit,

    /// <summary>Exception-queue cash a person put on the account.</summary>
    ExceptionCredit
}

public class SponsorAccountEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString();
    public SponsorAccountEntryType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTime PostedAt { get; set; } = DateTime.UtcNow;
    public string? BatchId { get; set; }
    public string? TraceNumber { get; set; }
    public string? InvoiceId { get; set; }
    public string? Reference { get; set; }

    [StringLength(500)]
    public string? Memo { get; set; }

    [StringLength(200)]
    public string? PostedBy { get; set; }
}
