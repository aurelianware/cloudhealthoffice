using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArService.Models;

/// <summary>
/// Running balance per GL account per period.
/// Tracks debits, credits, sponsor/member split, aging buckets, and reconciliation status.
/// </summary>
public class ArBalance
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string GlAccountId { get; set; } = string.Empty;

    [Required]
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// Period (first of month)
    /// </summary>
    [Required]
    public DateTime Period { get; set; }

    public decimal OpeningBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal ClosingBalance { get; set; }

    // Sponsor vs Member split (for premium receivable accounts)
    public decimal SponsorBalance { get; set; }
    public decimal MemberBalance { get; set; }
    public decimal SponsorDebits { get; set; }
    public decimal MemberDebits { get; set; }
    public decimal SponsorCredits { get; set; }
    public decimal MemberCredits { get; set; }

    // Aging buckets (days outstanding)
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }

    // Reconciliation
    public bool IsReconciled { get; set; }
    public DateTime? ReconciledAt { get; set; }
    public string? ReconciledBy { get; set; }
    public string? ReconciliationNotes { get; set; }

    /// <summary>
    /// Source tracking — what posted to this balance
    /// </summary>
    public List<ArPostingEntry> PostingEntries { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Individual posting entry within an AR balance record
/// </summary>
public class ArPostingEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString();
    public ArPostingSource Source { get; set; }
    public string? SourceReferenceId { get; set; }
    public string? SourceReferenceNumber { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public DateTime PostedAt { get; set; } = DateTime.UtcNow;
    public string? PostedBy { get; set; }
    public string? Memo { get; set; }

    /// <summary>
    /// Member this posting applies to (null for sponsor-level postings).
    /// Populated so <c>/api/v1/members/{memberId}/ar-summary</c> can aggregate
    /// charges/payments without cross-referencing claims or invoices.
    /// </summary>
    public string? MemberId { get; set; }
}

public enum ArPostingSource
{
    PremiumBillingRun = 1, CashReceipt = 2, ManualAdjustment = 3,
    WriteOff = 4, CapitationRun = 5, FfsPaymentRun = 6,
    EnrollmentRetro = 7, GracePeriodReinstatement = 8
}
