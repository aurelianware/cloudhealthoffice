using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CapitationService.Models;

/// <summary>
/// Represents an EFT/check disbursement to a capitated provider for a capitation statement.
/// The capitation equivalent of EftDraft — where EftDraft debits sponsors for premiums owed,
/// CapitationDisbursement credits providers for capitation payments earned.
/// Tracks the lifecycle from initiation through settlement or return.
/// </summary>
public class CapitationDisbursement
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
    /// Reference to the capitation statement being paid
    /// </summary>
    [Required]
    public string StatementId { get; set; } = string.Empty;

    /// <summary>
    /// Statement number for display
    /// </summary>
    public string StatementNumber { get; set; } = string.Empty;

    /// <summary>
    /// Provider NPI receiving the payment
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 10)]
    public string ProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized provider name for display
    /// </summary>
    [StringLength(300)]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Disbursement amount
    /// </summary>
    [Required]
    public decimal Amount { get; set; }

    /// <summary>
    /// Disbursement method
    /// </summary>
    [Required]
    public DisbursementMethod Method { get; set; }

    /// <summary>
    /// Current status of the disbursement
    /// </summary>
    [Required]
    public DisbursementStatus Status { get; set; } = DisbursementStatus.Pending;

    /// <summary>
    /// ACH trace number (for NACHA credits)
    /// </summary>
    public string? TraceNumber { get; set; }

    /// <summary>
    /// Stripe Transfer ID (when using Stripe Connect)
    /// </summary>
    public string? StripeTransferId { get; set; }

    /// <summary>
    /// NACHA file reference (filename/ID of the generated NACHA credit file)
    /// </summary>
    public string? NachaFileReference { get; set; }

    /// <summary>
    /// Check number (when paying by paper check)
    /// </summary>
    [StringLength(50)]
    public string? CheckNumber { get; set; }

    /// <summary>
    /// Bank routing number (last 4 digits only, for reference)
    /// </summary>
    public string? RoutingNumberLast4 { get; set; }

    /// <summary>
    /// Bank account number (last 4 digits only, for reference)
    /// </summary>
    public string? AccountNumberLast4 { get; set; }

    /// <summary>
    /// When the disbursement was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the disbursement was submitted to bank/Stripe
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
    /// If returned/failed, the ACH return code (e.g. R01=Insufficient Funds, R03=No Account)
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
    /// Maximum retries allowed (configurable per provider)
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// User/system that initiated the disbursement
    /// </summary>
    public string? InitiatedBy { get; set; }

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error details for failed disbursements
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Disbursement method for capitation payments to providers
/// </summary>
public enum DisbursementMethod
{
    /// <summary>
    /// NACHA ACH credit (bank file submission)
    /// </summary>
    NachaCredit,

    /// <summary>
    /// Stripe Connect payout
    /// </summary>
    StripeConnect,

    /// <summary>
    /// Paper check
    /// </summary>
    Check
}

/// <summary>
/// Lifecycle status of a capitation disbursement
/// </summary>
public enum DisbursementStatus
{
    /// <summary>
    /// Disbursement created, not yet submitted
    /// </summary>
    Pending,

    /// <summary>
    /// Submitted to bank (NACHA) or Stripe Connect
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
    /// Returned by bank (account closed, invalid account, etc.)
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
/// Request to initiate a disbursement for a capitation statement
/// </summary>
public class InitiateDisbursementRequest
{
    /// <summary>
    /// Statement ID to disburse
    /// </summary>
    [Required]
    public string StatementId { get; set; } = string.Empty;

    /// <summary>
    /// Override disbursement method (if not set, uses provider's preferred method)
    /// </summary>
    public DisbursementMethod? Method { get; set; }

    /// <summary>
    /// Override amount (defaults to statement NetPayable)
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// Who is initiating this disbursement
    /// </summary>
    public string? InitiatedBy { get; set; }
}

/// <summary>
/// Request to initiate disbursements for all eligible statements in a capitation run
/// </summary>
public class InitiateBatchDisbursementRequest
{
    /// <summary>
    /// Capitation run ID (disburses all eligible statements)
    /// </summary>
    public string? CapitationRunId { get; set; }

    /// <summary>
    /// Or specific statement IDs
    /// </summary>
    public List<string> StatementIds { get; set; } = new();

    /// <summary>
    /// Override disbursement method for all
    /// </summary>
    public DisbursementMethod? Method { get; set; }

    /// <summary>
    /// Who is initiating
    /// </summary>
    public string? InitiatedBy { get; set; }
}

/// <summary>
/// Request to process an ACH return on a disbursement
/// </summary>
public class ProcessReturnRequest
{
    /// <summary>
    /// Disbursement ID
    /// </summary>
    [Required]
    public string DisbursementId { get; set; } = string.Empty;

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
/// Result of a NACHA credit file generation for provider disbursements
/// </summary>
public class NachaCreditFileResult
{
    /// <summary>
    /// Unique file reference ID
    /// </summary>
    public string FileReference { get; set; } = string.Empty;

    /// <summary>
    /// Generated filename
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Raw NACHA file content
    /// </summary>
    public string FileContent { get; set; } = string.Empty;

    /// <summary>
    /// Number of credit entries in the file
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>
    /// Total dollar amount of all credits
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// When the file was generated
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Summary of a batch disbursement operation
/// </summary>
public class BatchDisbursementResult
{
    /// <summary>
    /// Total statements processed
    /// </summary>
    public int TotalStatements { get; set; }

    /// <summary>
    /// Number of disbursements initiated
    /// </summary>
    public int DisbursementsInitiated { get; set; }

    /// <summary>
    /// Number of statements skipped (no bank account, EFT disabled, etc.)
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Number of errors encountered
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Total amount disbursed
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// IDs of created disbursement records
    /// </summary>
    public List<string> DisbursementIds { get; set; } = new();

    /// <summary>
    /// Error messages for failed disbursements
    /// </summary>
    public List<string> ErrorMessages { get; set; } = new();

    /// <summary>
    /// Generated NACHA credit file (if any disbursements used NACHA)
    /// </summary>
    public NachaCreditFileResult? NachaFile { get; set; }
}
