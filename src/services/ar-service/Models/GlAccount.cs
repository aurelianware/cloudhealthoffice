using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArService.Models;

/// <summary>
/// GL Account master record — chart of accounts entry with QNXT segment code parity.
/// Defines account number, type, segment codes, LOB mapping, and premium split configuration.
/// </summary>
public class GlAccount
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(20)]
    public string AccountNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string AccountName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// QNXT COA Segment Codes (all six segments)
    /// </summary>
    [Required]
    public GlSegmentCodes Segments { get; set; } = new();

    [Required]
    public GlAccountType AccountType { get; set; }

    [Required]
    public GlNormalBalance NormalBalance { get; set; }

    public GlAccountSubType? SubType { get; set; }

    public FinancialStatementSection? StatementSection { get; set; }

    /// <summary>
    /// Which lines of business post to this account
    /// </summary>
    public List<LineOfBusiness> LineOfBusinessMapping { get; set; } = new();

    /// <summary>
    /// Defines how premiums are allocated between sponsor and member portions
    /// </summary>
    public PremiumSplitConfig? PremiumSplit { get; set; }

    /// <summary>
    /// Batch posting rules — controls auto-posting from billing runs
    /// </summary>
    public List<string> BatchRuleIds { get; set; } = new();

    public bool IsReconciliationAccount { get; set; }

    /// <summary>
    /// Reconciliation pairing (e.g. AR control account paired with clearing account)
    /// </summary>
    public string? ReconciliationPairAccountId { get; set; }

    public bool IsIntercompany { get; set; }

    [StringLength(20)]
    public string? IntercompanyEntityCode { get; set; }

    [Required]
    public GlAccountStatus Status { get; set; } = GlAccountStatus.Active;

    [Required]
    public DateTime EffectiveDate { get; set; }

    public DateTime? TerminationDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// Standard six-segment COA hierarchy (QNXT segment code parity)
/// </summary>
public class GlSegmentCodes
{
    [StringLength(10)]
    public string Company { get; set; } = string.Empty;

    [StringLength(10)]
    public string Fund { get; set; } = string.Empty;

    [StringLength(10)]
    public string Department { get; set; } = string.Empty;

    [StringLength(10)]
    public string Program { get; set; } = string.Empty;

    [StringLength(20)]
    public string Account { get; set; } = string.Empty;

    [StringLength(10)]
    public string SubAccount { get; set; } = string.Empty;

    /// <summary>
    /// Produces fully qualified segment string e.g. "01-GEN-ADMIN-HMO-4010-00"
    /// </summary>
    public string ToQualifiedString() =>
        $"{Company}-{Fund}-{Department}-{Program}-{Account}-{SubAccount}";
}

public class PremiumSplitConfig
{
    public decimal SponsorPercentage { get; set; }
    public decimal MemberPercentage { get; set; }
    public bool IsPlanSpecific { get; set; }
    public List<PlanSplitOverride> PlanOverrides { get; set; } = new();
}

public class PlanSplitOverride
{
    [Required]
    public string PlanId { get; set; } = string.Empty;

    public decimal SponsorPercentage { get; set; }
    public decimal MemberPercentage { get; set; }
}

public enum GlAccountType { Asset = 1, Liability = 2, Revenue = 3, Expense = 4, Equity = 5 }

public enum GlNormalBalance { Debit = 1, Credit = 2 }

public enum GlAccountSubType
{
    PremiumReceivable = 1, SponsorReceivable = 2, MemberReceivable = 3,
    ClearingAccount = 4, WriteOffAccount = 5, ProviderPayable = 6,
    CapitationPayable = 7, FfsPayable = 8, UnearnedPremium = 9,
    RetroAdjustment = 10
}

public enum FinancialStatementSection
{
    CurrentAssets = 1, LongTermAssets = 2, CurrentLiabilities = 3,
    LongTermLiabilities = 4, MedicalRevenue = 5, AdminRevenue = 6,
    MedicalExpense = 7, AdminExpense = 8
}

public enum GlAccountStatus { Active = 1, Inactive = 2, Suspended = 3 }

public enum LineOfBusiness
{
    Commercial = 1, Medicare = 2, Medicaid = 3,
    Exchange = 4, TRICARE = 5, VA = 6
}
