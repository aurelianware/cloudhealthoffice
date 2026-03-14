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
    /// Last updated timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error details for failed drafts
    /// </summary>
    public string? ErrorMessage { get; set; }
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
    Cancelled
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
    /// Who is initiating this draft
    /// </summary>
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
    /// Who is initiating
    /// </summary>
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
/// Result of a NACHA file generation
/// </summary>
public class NachaFileResult
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileContent { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
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
}
