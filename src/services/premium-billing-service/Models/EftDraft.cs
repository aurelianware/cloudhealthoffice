using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PremiumBillingService.Models;

/// <summary>
/// Represents an EFT/ACH draft attempt against a sponsor's bank account for a premium invoice.
/// Tracks the lifecycle of a single draft from initiation through settlement or return.
/// </summary>
public class EftDraft
{
    /// <summary>
    /// Multi-tenant partition key
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Reference to the invoice being drafted
    /// </summary>
    [Required]
    public string InvoiceId { get; set; } = string.Empty;

    /// <summary>
    /// Invoice number for display
    /// </summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Sponsor group number
    /// </summary>
    [Required]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// Amount to draft
    /// </summary>
    [Required]
    public decimal Amount { get; set; }

    /// <summary>
    /// Draft method: NACHA or StripeACH
    /// </summary>
    [Required]
    public EftMethod Method { get; set; }

    /// <summary>
    /// Current status of the draft
    /// </summary>
    [Required]
    public EftDraftStatus Status { get; set; } = EftDraftStatus.Pending;

    /// <summary>
    /// ACH trace number (NACHA) or Stripe PaymentIntent ID
    /// </summary>
    public string? TraceNumber { get; set; }

    /// <summary>
    /// Stripe PaymentIntent ID (when using Stripe ACH)
    /// </summary>
    public string? StripePaymentIntentId { get; set; }

    /// <summary>
    /// NACHA batch ID if included in a NACHA file
    /// </summary>
    public string? NachaBatchId { get; set; }

    /// <summary>
    /// NACHA file reference (filename/ID of the generated NACHA file)
    /// </summary>
    public string? NachaFileReference { get; set; }

    /// <summary>
    /// Bank routing number (last 4 digits only, for reference)
    /// </summary>
    public string? RoutingNumberLast4 { get; set; }

    /// <summary>
    /// Bank account number (last 4 digits only, for reference)
    /// </summary>
    public string? AccountNumberLast4 { get; set; }

    /// <summary>
    /// When the draft was initiated
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the draft was submitted to bank/Stripe
    /// </summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>
    /// Expected settlement date (typically T+2 business days for ACH)
    /// </summary>
    public DateTime? ExpectedSettlementDate { get; set; }

    /// <summary>
    /// Actual settlement date
    /// </summary>
    public DateTime? SettledAt { get; set; }

    /// <summary>
    /// If returned/failed, the return code (e.g. R01=Insufficient Funds, R02=Account Closed)
    /// </summary>
    public string? ReturnCode { get; set; }

    /// <summary>
    /// Human-readable return reason
    /// </summary>
    public string? ReturnReason { get; set; }

    /// <summary>
    /// When the return was received
    /// </summary>
    public DateTime? ReturnedAt { get; set; }

    /// <summary>
    /// Number of retry attempts
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum retries allowed (configurable per sponsor)
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// User/system that initiated the draft
    /// </summary>
    public string? InitiatedBy { get; set; }

    /// <summary>
    /// Who last changed the draft (settle, return, cancel): token subject, or
    /// "stripe-webhook" for changes driven by Stripe events.
    /// </summary>
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error details for failed drafts
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The NACHA release that claimed this draft (Pending to Releasing).</summary>
    public string? ReleaseClaimId { get; set; }

    /// <summary>When that release claimed it.</summary>
    public DateTime? ReleaseClaimedAt { get; set; }

    /// <summary>
    /// The invoice id while this draft is active (not Settled, Returned, Failed
    /// or Cancelled), otherwise null. Set by the repository on every write: at
    /// most one active draft per invoice (Mongo: unique partial index on
    /// TenantId + ActiveInvoiceKey; Cosmos: a lock item per invoice).
    /// </summary>
    public string? ActiveInvoiceKey { get; set; }

    /// <summary>
    /// Active: may still debit the sponsor. An invoice has at most one active
    /// draft, so two releases never debit it twice.
    /// </summary>
    public static bool IsActive(EftDraftStatus status) => status is not
        (EftDraftStatus.Settled or EftDraftStatus.Returned or EftDraftStatus.Failed or EftDraftStatus.Cancelled);

    /// <summary>Statuses in which a draft holds its invoice.</summary>
    public static readonly EftDraftStatus[] ActiveStatuses =
        Enum.GetValues<EftDraftStatus>().Where(IsActive).ToArray();

    /// <summary>Sets <see cref="ActiveInvoiceKey"/> from the status.</summary>
    public void RefreshActiveInvoiceKey() => ActiveInvoiceKey = IsActive(Status) ? InvoiceId : null;
}

/// <summary>
/// The invoice already has an active draft (409): an invoice is debited by at
/// most one draft at a time, so a second draft or batch never debits it twice.
/// </summary>
public sealed class InvoiceDraftConflictException : Exception
{
    public InvoiceDraftConflictException(string invoiceId)
        : base($"Invoice {invoiceId} already has an active EFT draft; it is not drafted again until that one is settled, returned, failed or cancelled.")
    {
        InvoiceId = invoiceId;
    }

    public string InvoiceId { get; }
}

/// <summary>
/// EFT draft method
/// </summary>
public enum EftMethod
{
    /// <summary>
    /// NACHA file-based ACH (bank submission)
    /// </summary>
    Nacha,

    /// <summary>
    /// Stripe ACH Direct Debit
    /// </summary>
    StripeAch
}

/// <summary>
/// Lifecycle status of an EFT draft
/// </summary>
public enum EftDraftStatus
{
    /// <summary>
    /// Draft created, not yet submitted
    /// </summary>
    Pending,

    /// <summary>
    /// Submitted to bank (NACHA) or Stripe
    /// </summary>
    Submitted,

    /// <summary>
    /// Processing at the bank/Stripe
    /// </summary>
    Processing,

    /// <summary>
    /// Successfully settled
    /// </summary>
    Settled,

    /// <summary>
    /// Returned by bank (NSF, account closed, etc.)
    /// </summary>
    Returned,

    /// <summary>
    /// Failed to submit or process
    /// </summary>
    Failed,

    /// <summary>
    /// Cancelled before settlement
    /// </summary>
    Cancelled,

    /// <summary>
    /// In a NACHA file that could not be sent to the bank (transmission not
    /// configured or failed). The file is held encrypted for 7 days: a
    /// platform admin must retrieve it or another approver retry it.
    /// </summary>
    AwaitingRetrieval,

    /// <summary>
    /// Claimed by one NACHA release (<see cref="EftDraft.ReleaseClaimId"/>) and
    /// being put in a file: no other release may include it. Moved from Pending
    /// by a conditional write before the file is built; it ends Submitted,
    /// AwaitingRetrieval, or back in Pending when nothing was sent. A draft left
    /// here (the process stopped mid-send) needs checking with the bank.
    /// </summary>
    Releasing,

    /// <summary>
    /// In a NACHA file that may have reached the bank (upload finished, rename
    /// outcome unknown). Never re-sent, retried or retrieved until a user with
    /// payments:approve records what the bank says (Submitted, or back to
    /// AwaitingRetrieval / Pending).
    /// </summary>
    DeliveryUnknown,

    /// <summary>
    /// A Stripe debit whose outcome is unknown (the call failed after it was
    /// sent): it may have been made. It keeps the invoice until someone checks
    /// Stripe and records the answer.
    /// </summary>
    PaymentUnknown
}

/// <summary>
/// Sponsor bank account information for EFT drafts.
/// Fields returned from sponsor-service; actual bank details are tokenized/vaulted there.
/// </summary>
public class SponsorBankAccount
{
    /// <summary>
    /// Whether the sponsor has EFT/auto-draft enabled
    /// </summary>
    public bool EftEnabled { get; set; }

    /// <summary>
    /// Preferred EFT method (Nacha or StripeAch)
    /// </summary>
    public EftMethod? PreferredMethod { get; set; }

    /// <summary>
    /// Bank routing number (9-digit ABA, stored in sponsor-service vault)
    /// </summary>
    public string? RoutingNumber { get; set; }

    /// <summary>
    /// Bank account number (stored in sponsor-service vault)
    /// </summary>
    public string? AccountNumber { get; set; }

    /// <summary>
    /// Account type
    /// </summary>
    public BankAccountType AccountType { get; set; } = BankAccountType.Checking;

    /// <summary>
    /// Name on the bank account
    /// </summary>
    public string? AccountHolderName { get; set; }

    /// <summary>
    /// Stripe customer ID (if using Stripe ACH)
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Stripe bank account or payment method ID (if using Stripe ACH)
    /// </summary>
    public string? StripePaymentMethodId { get; set; }

    /// <summary>
    /// Last 4 digits of routing number (for display)
    /// </summary>
    public string? RoutingNumberLast4 { get; set; }

    /// <summary>
    /// Last 4 digits of account number (for display)
    /// </summary>
    public string? AccountNumberLast4 { get; set; }
}

public enum BankAccountType
{
    Checking,
    Savings
}

/// <summary>
/// Request to initiate an EFT draft for an invoice
/// </summary>
public class InitiateEftDraftRequest
{
    /// <summary>
    /// Invoice ID to draft
    /// </summary>
    [Required]
    public string InvoiceId { get; set; } = string.Empty;

    /// <summary>
    /// Override draft method (if not set, uses sponsor's preferred method)
    /// </summary>
    public EftMethod? Method { get; set; }

    /// <summary>
    /// Override amount (defaults to invoice BalanceDue)
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// Who is initiating this draft. Ignored from the body: set from the token.
    /// </summary>
    [JsonIgnore]
    public string? InitiatedBy { get; set; }
}

/// <summary>
/// Request to initiate EFT drafts for all eligible invoices in a billing run
/// </summary>
public class InitiateBatchEftRequest
{
    /// <summary>
    /// Billing run ID (drafts all eligible invoices)
    /// </summary>
    public string? BillingRunId { get; set; }

    /// <summary>
    /// Or specific invoice IDs
    /// </summary>
    public List<string> InvoiceIds { get; set; } = new();

    /// <summary>
    /// Override draft method for all
    /// </summary>
    public EftMethod? Method { get; set; }

    /// <summary>
    /// Who is initiating. Ignored from the body: set from the token.
    /// </summary>
    [JsonIgnore]
    public string? InitiatedBy { get; set; }
}

/// <summary>
/// Request to process an ACH return
/// </summary>
public class ProcessAchReturnRequest
{
    /// <summary>
    /// EFT draft ID
    /// </summary>
    [Required]
    public string DraftId { get; set; } = string.Empty;

    /// <summary>
    /// ACH return code (e.g. R01, R02, R03)
    /// </summary>
    [Required]
    public string ReturnCode { get; set; } = string.Empty;

    /// <summary>
    /// Return reason description
    /// </summary>
    public string? ReturnReason { get; set; }
}

/// <summary>
/// A generated NACHA debit file. Holds full routing and account numbers: it
/// never leaves the service except to the bank (INachaDispatcher). Not an API type.
/// </summary>
public class GeneratedNachaFile
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileContent { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What an approver gets back for a NACHA file: a masked summary and the
/// transmission receipt. Never the file, never a full routing or account number.
/// </summary>
public class NachaFileResult
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalDebitAmount { get; set; }
    public decimal TotalCreditAmount { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Transmitted, AwaitingRetrieval or NotSent.</summary>
    public string TransmissionStatus { get; set; } = string.Empty;

    /// <summary>Why the file was not delivered (AwaitingRetrieval, NotSent).</summary>
    public string? TransmissionError { get; set; }

    /// <summary>When a held file is deleted (AwaitingRetrieval).</summary>
    public DateTime? HeldUntil { get; set; }

    /// <summary>Set when the bank received the file.</summary>
    public CloudHealthOffice.NachaTransmission.NachaTransmissionReceipt? Receipt { get; set; }

    /// <summary>One line per debit: sponsor, last 4 and amount.</summary>
    public List<NachaEntrySummary> Entries { get; set; } = new();

    /// <summary>Pending drafts left out of the file because something needs fixing first.</summary>
    public List<EftAttentionItem> NeedsAttention { get; set; } = new();
}

/// <summary>One debit in a NACHA file, masked.</summary>
public class NachaEntrySummary
{
    public string DraftId { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string? AccountHolderName { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
    public decimal Amount { get; set; }
    public string? TraceNumber { get; set; }
}

/// <summary>
/// An invoice or draft that could not be drafted for a reason someone has to
/// fix (as opposed to a normal skip such as a paid invoice).
/// </summary>
public class EftAttentionItem
{
    public string? InvoiceId { get; set; }
    public string? DraftId { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Summary of a batch EFT operation
/// </summary>
public class BatchEftResult
{
    public int TotalInvoices { get; set; }
    public int DraftsInitiated { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public decimal TotalAmount { get; set; }
    public List<string> DraftIds { get; set; } = new();
    public List<string> ErrorMessages { get; set; } = new();
    public NachaFileResult? NachaFile { get; set; }

    /// <summary>Invoices that were not drafted for a reason someone has to fix (also counted in Errors).</summary>
    public List<EftAttentionItem> NeedsAttention { get; set; } = new();
}
