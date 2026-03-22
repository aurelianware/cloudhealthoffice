using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CapitationService.Models;

/// <summary>
/// Provider-facing capitation payment statement.
/// The capitation equivalent of PremiumInvoice — where PremiumInvoice bills sponsors
/// for premiums owed TO the plan, CapitationStatement details payments owed BY the plan
/// TO a capitated provider for their assigned member panel.
/// </summary>
public class CapitationStatement
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-facing statement number (e.g. "CAPSTMT-1234567890-2026-03")
    /// Format: CAPSTMT-{NPI}-{yyyy-MM}
    /// </summary>
    [Required]
    [StringLength(100)]
    public string StatementNumber { get; set; } = string.Empty;

    /// <summary>
    /// Reference to the capitation run that generated this statement
    /// </summary>
    public string? CapitationRunId { get; set; }

    /// <summary>
    /// Reference to the capitation contract governing rates and terms
    /// </summary>
    [Required]
    public string ContractId { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized contract number for display
    /// </summary>
    [StringLength(50)]
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>
    /// Provider NPI (10-digit National Provider Identifier)
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
    /// Start of the capitation period (first of month)
    /// </summary>
    [Required]
    public DateTime CapitationPeriodStart { get; set; }

    /// <summary>
    /// End of the capitation period (last day of month)
    /// </summary>
    [Required]
    public DateTime CapitationPeriodEnd { get; set; }

    /// <summary>
    /// Current statement status
    /// </summary>
    [Required]
    public CapitationStatementStatus Status { get; set; } = CapitationStatementStatus.Generated;

    /// <summary>
    /// Individual member capitation line items
    /// </summary>
    public List<CapitationLineItem> LineItems { get; set; } = new();

    /// <summary>
    /// Retroactive and other adjustments
    /// </summary>
    public List<CapitationAdjustment> Adjustments { get; set; } = new();

    /// <summary>
    /// Total member-months on this statement
    /// </summary>
    public int MemberMonths { get; set; }

    /// <summary>
    /// Sum of all line item gross amounts (before withhold)
    /// </summary>
    public decimal GrossCapitation { get; set; }

    /// <summary>
    /// Total quality withhold amount held back
    /// </summary>
    public decimal WithholdAmount { get; set; }

    /// <summary>
    /// Sum of all adjustments (positive or negative)
    /// </summary>
    public decimal TotalAdjustments { get; set; }

    /// <summary>
    /// Net amount payable to provider (GrossCapitation - WithholdAmount + TotalAdjustments)
    /// </summary>
    public decimal NetPayable { get; set; }

    /// <summary>
    /// Date payment was issued
    /// </summary>
    public DateTime? PaymentDate { get; set; }

    /// <summary>
    /// Reference to the EFT disbursement record (if paid via EFT)
    /// </summary>
    public string? EftDisbursementId { get; set; }

    /// <summary>
    /// Check number (if paid by paper check)
    /// </summary>
    [StringLength(50)]
    public string? CheckNumber { get; set; }

    /// <summary>
    /// ERA (835) control number for electronic remittance advice
    /// </summary>
    [StringLength(50)]
    public string? EraControlNumber { get; set; }

    /// <summary>
    /// Record creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Recalculate computed totals from line items and adjustments.
    /// Mirrors PremiumInvoice.RecalculateTotals() pattern.
    /// </summary>
    public void RecalculateTotals()
    {
        MemberMonths = LineItems.Count;
        GrossCapitation = LineItems.Sum(li => li.GrossAmount);
        WithholdAmount = LineItems.Sum(li => li.WithholdAmount);
        TotalAdjustments = Adjustments.Sum(a => a.Amount);
        NetPayable = GrossCapitation - WithholdAmount + TotalAdjustments;
    }
}

/// <summary>
/// Individual member capitation line item on a statement.
/// Represents one member-month of capitation payment, with risk adjustment
/// and proration applied to the base PMPM rate.
/// </summary>
public class CapitationLineItem
{
    /// <summary>
    /// Member ID from member-service
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized member name for display
    /// </summary>
    [StringLength(200)]
    public string MemberName { get; set; } = string.Empty;

    /// <summary>
    /// Coverage record ID from coverage-service
    /// </summary>
    [StringLength(50)]
    public string? CoverageId { get; set; }

    /// <summary>
    /// Benefit plan ID
    /// </summary>
    [StringLength(50)]
    public string? PlanId { get; set; }

    /// <summary>
    /// Actuarial age/sex category used for rate lookup
    /// </summary>
    public AgeSexCategory? AgeSexCategory { get; set; }

    /// <summary>
    /// Member age at time of capitation period
    /// </summary>
    public int MemberAge { get; set; }

    /// <summary>
    /// Member gender (M/F/U)
    /// </summary>
    [StringLength(1)]
    public string? Gender { get; set; }

    /// <summary>
    /// Base PMPM rate from the contract rate tier (before risk adjustment)
    /// </summary>
    public decimal BasePMPM { get; set; }

    /// <summary>
    /// Member-level risk score (HCC/RAF). 1.0 = average risk.
    /// </summary>
    public decimal RiskScore { get; set; } = 1.0m;

    /// <summary>
    /// Risk-adjusted PMPM (BasePMPM × RiskScore)
    /// </summary>
    public decimal AdjustedPMPM { get; set; }

    /// <summary>
    /// Proration factor for partial months (1.0 = full month, 0.5 = half month)
    /// </summary>
    public decimal ProrationFactor { get; set; } = 1.0m;

    /// <summary>
    /// Gross capitation amount (AdjustedPMPM × ProrationFactor)
    /// </summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// Quality withhold amount held back for this member-month
    /// </summary>
    public decimal WithholdAmount { get; set; }

    /// <summary>
    /// Net amount payable for this member-month (GrossAmount - WithholdAmount)
    /// </summary>
    public decimal NetAmount { get; set; }

    /// <summary>
    /// Date the member was assigned to this PCP
    /// </summary>
    public DateTime AssignmentEffectiveDate { get; set; }

    /// <summary>
    /// Date the member's assignment to this PCP ended (null if still active)
    /// </summary>
    public DateTime? AssignmentTermDate { get; set; }

    /// <summary>
    /// Whether this line item is a retroactive adjustment (e.g. retro enrollment/disenrollment)
    /// </summary>
    public bool IsRetroactive { get; set; }

    /// <summary>
    /// Reason for retroactive adjustment if applicable
    /// </summary>
    [StringLength(500)]
    public string? AdjustmentReason { get; set; }
}

/// <summary>
/// Adjustment on a capitation statement (retro enrollments, risk score updates,
/// withhold releases, incentive payments, stop-loss credits, etc.)
/// </summary>
public class CapitationAdjustment
{
    /// <summary>
    /// Type of adjustment
    /// </summary>
    [Required]
    public CapitationAdjustmentType Type { get; set; }

    /// <summary>
    /// Human-readable description of the adjustment
    /// </summary>
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Adjustment amount (positive = additional payment, negative = recoupment)
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Related member ID if the adjustment is member-specific
    /// </summary>
    [StringLength(50)]
    public string? RelatedMemberId { get; set; }

    /// <summary>
    /// The capitation period this adjustment relates to (may differ from statement period for retros)
    /// </summary>
    public DateTime? RelatedPeriod { get; set; }

    /// <summary>
    /// Date the adjustment was applied
    /// </summary>
    public DateTime AdjustmentDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Capitation statement lifecycle status
/// </summary>
public enum CapitationStatementStatus
{
    /// <summary>
    /// Statement generated by capitation run, pending review
    /// </summary>
    Generated,

    /// <summary>
    /// Statement reviewed and approved for payment
    /// </summary>
    Approved,

    /// <summary>
    /// Payment has been initiated (EFT submitted or check cut)
    /// </summary>
    PaymentInitiated,

    /// <summary>
    /// Payment settled / check cashed
    /// </summary>
    Paid,

    /// <summary>
    /// Statement voided (e.g. duplicate, error)
    /// </summary>
    Voided,

    /// <summary>
    /// Statement on hold pending investigation
    /// </summary>
    OnHold
}

/// <summary>
/// Types of capitation adjustments
/// </summary>
public enum CapitationAdjustmentType
{
    /// <summary>
    /// Retroactive member enrollment (member added to panel for a prior period)
    /// </summary>
    RetroEnrollment,

    /// <summary>
    /// Retroactive member disenrollment (member removed from panel for a prior period)
    /// </summary>
    RetroDisenrollment,

    /// <summary>
    /// Risk score updated for a prior period (HCC/RAF recalculation)
    /// </summary>
    RiskScoreUpdate,

    /// <summary>
    /// Contract rate correction for a prior period
    /// </summary>
    RateCorrection,

    /// <summary>
    /// Release of previously withheld funds (quality metrics met)
    /// </summary>
    WithholdRelease,

    /// <summary>
    /// Incentive pool payment (bonus for quality/performance targets)
    /// </summary>
    IncentivePayment,

    /// <summary>
    /// Stop-loss credit (plan absorbs costs above threshold)
    /// </summary>
    StopLossCredit,

    /// <summary>
    /// Other adjustment not covered by specific types
    /// </summary>
    Other
}
