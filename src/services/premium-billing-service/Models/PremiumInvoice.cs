using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PremiumBillingService.Models;

/// <summary>
/// Represents a premium billing invoice sent to a sponsor (employer group)
/// for the monthly insurance premiums of their enrolled employees.
/// </summary>
public class PremiumInvoice
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
    /// User-facing invoice number (e.g. "INV-GRP001-2026-03")
    /// </summary>
    [Required]
    [StringLength(100)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Reference to the billing run that generated this invoice
    /// </summary>
    public string? BillingRunId { get; set; }

    /// <summary>
    /// Sponsor group number (FK to sponsor-service)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized sponsor/employer name for display
    /// </summary>
    [StringLength(200)]
    public string SponsorName { get; set; } = string.Empty;

    /// <summary>
    /// Start of the billing period
    /// </summary>
    [Required]
    public DateTime BillingPeriodStart { get; set; }

    /// <summary>
    /// End of the billing period
    /// </summary>
    [Required]
    public DateTime BillingPeriodEnd { get; set; }

    /// <summary>
    /// Current invoice status
    /// </summary>
    [Required]
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Generated;

    /// <summary>
    /// Payment due date
    /// </summary>
    [Required]
    public DateTime DueDate { get; set; }

    /// <summary>
    /// Individual member premium line items
    /// </summary>
    public List<InvoiceLineItem> LineItems { get; set; } = new();

    /// <summary>
    /// Retroactive adjustments (mid-month adds/terms, rate changes)
    /// </summary>
    public List<InvoiceAdjustment> Adjustments { get; set; } = new();

    /// <summary>
    /// Payments received against this invoice
    /// </summary>
    public List<InvoicePayment> Payments { get; set; } = new();

    /// <summary>
    /// Sum of all line item premiums
    /// </summary>
    public decimal SubtotalPremium { get; set; }

    /// <summary>
    /// Sum of all adjustments (positive or negative)
    /// </summary>
    public decimal TotalAdjustments { get; set; }

    /// <summary>
    /// Total invoice amount (SubtotalPremium + TotalAdjustments)
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Sum of all payments received
    /// </summary>
    public decimal TotalPaid { get; set; }

    /// <summary>
    /// Outstanding balance (TotalAmount - TotalPaid)
    /// </summary>
    public decimal BalanceDue { get; set; }

    /// <summary>
    /// Number of members on this invoice
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// Grace period in days before delinquency (from sponsor billing config)
    /// </summary>
    public int GracePeriodDays { get; set; } = 30;

    /// <summary>
    /// Date when grace period expires (DueDate + GracePeriodDays for
    /// Standard, DueDate + 90 days for ACA APTC-subsidized invoices).
    /// </summary>
    public DateTime? GracePeriodExpires { get; set; }

    /// <summary>
    /// True when the member on this invoice receives an ACA Advance Premium
    /// Tax Credit subsidy. APTC-subsidized members have a statutory 3-month
    /// grace period (45 CFR §156.270(d)) that differs from the standard
    /// commercial grace window — this flag drives that distinction.
    /// </summary>
    public bool IsAptcSubsidized { get; set; }

    /// <summary>
    /// Advance Premium Tax Credit amount applied to the subscriber's monthly
    /// premium. Populated only when <see cref="IsAptcSubsidized"/> is true.
    /// </summary>
    public decimal AptcMonthlyAmount { get; set; }

    /// <summary>
    /// Which grace-period regime applies to this invoice. Drives the portal
    /// grace banner copy (APTC 3-month message vs. standard grace message).
    /// </summary>
    public GraceType GraceType { get; set; } = GraceType.Standard;

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
    /// Recalculate computed totals from line items, adjustments, and payments
    /// </summary>
    public void RecalculateTotals()
    {
        SubtotalPremium = LineItems.Sum(li => li.TotalPremium);
        TotalAdjustments = Adjustments.Sum(a => a.Amount);
        TotalAmount = SubtotalPremium + TotalAdjustments;
        TotalPaid = Payments.Sum(p => p.Amount);
        BalanceDue = TotalAmount - TotalPaid;
        MemberCount = LineItems.Select(li => li.MemberId).Distinct().Count();
    }
}

/// <summary>
/// Individual member premium line item on an invoice
/// </summary>
public class InvoiceLineItem
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
    /// Coverage level (EMP, ESP, ECH, FAM)
    /// </summary>
    [StringLength(3)]
    public string? CoverageLevel { get; set; }

    /// <summary>
    /// Insurance line code (HLT, DEN, VIS)
    /// </summary>
    [StringLength(3)]
    public string? InsuranceLineCode { get; set; }

    /// <summary>
    /// Employee/subscriber premium portion
    /// </summary>
    public decimal SubscriberPremium { get; set; }

    /// <summary>
    /// Employer contribution portion
    /// </summary>
    public decimal EmployerContribution { get; set; }

    /// <summary>
    /// Total premium for this line (SubscriberPremium + EmployerContribution)
    /// </summary>
    public decimal TotalPremium { get; set; }

    /// <summary>
    /// Coverage effective date
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Coverage termination date (null if still active)
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Proration factor (1.0 = full month, 0.5 = half month, etc.)
    /// </summary>
    public decimal ProrationFactor { get; set; } = 1.0m;

    /// <summary>
    /// Whether this line item is a retroactive add/change
    /// </summary>
    public bool IsRetroactive { get; set; }

    /// <summary>
    /// Reason for adjustment if retroactive
    /// </summary>
    [StringLength(500)]
    public string? AdjustmentReason { get; set; }
}

/// <summary>
/// Retroactive adjustment on an invoice
/// </summary>
public class InvoiceAdjustment
{
    /// <summary>
    /// Adjustment type
    /// </summary>
    [Required]
    public AdjustmentType Type { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Adjustment amount (positive = charge, negative = credit)
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Related member ID if applicable
    /// </summary>
    [StringLength(50)]
    public string? RelatedMemberId { get; set; }

    /// <summary>
    /// Date the adjustment applies to
    /// </summary>
    public DateTime AdjustmentDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Payment received against an invoice
/// </summary>
public class InvoicePayment
{
    /// <summary>
    /// Unique payment identifier
    /// </summary>
    public string PaymentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Payment amount
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Date the payment was made
    /// </summary>
    public DateTime PaymentDate { get; set; }

    /// <summary>
    /// Payment method (ACH, Wire, Check)
    /// </summary>
    [StringLength(50)]
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// Check number, wire reference, or ACH trace number
    /// </summary>
    [StringLength(100)]
    public string? ReferenceNumber { get; set; }

    /// <summary>
    /// Date payment was received/posted
    /// </summary>
    public DateTime ReceivedDate { get; set; } = DateTime.UtcNow;
}

public enum InvoiceStatus
{
    Generated,
    Sent,
    PartiallyPaid,
    Paid,
    Overdue,
    Delinquent,
    Voided,
    WriteOff
}

/// <summary>
/// Grace-period regime that applies to an invoice.
/// </summary>
public enum GraceType
{
    /// <summary>Standard commercial grace window (see sponsor BillingInfo).</summary>
    Standard = 0,

    /// <summary>
    /// ACA APTC 3-month statutory grace for Exchange QHP enrollees receiving
    /// an advance premium tax credit (45 CFR §156.270(d)).
    /// </summary>
    AptcThreeMonth = 1
}

public enum AdjustmentType
{
    RetroAdd,
    RetroTerm,
    RateChange,
    Credit,
    Other
}

/// <summary>
/// Request DTO for recording a payment against an invoice
/// </summary>
public class RecordPaymentRequest
{
    [Required]
    public decimal Amount { get; set; }

    [Required]
    public DateTime PaymentDate { get; set; }

    [StringLength(50)]
    public string? PaymentMethod { get; set; }

    [StringLength(100)]
    public string? ReferenceNumber { get; set; }
}

/// <summary>
/// Aging report summary
/// </summary>
public class AgingReport
{
    public decimal CurrentAmount { get; set; }
    public int CurrentCount { get; set; }
    public decimal ThirtyDayAmount { get; set; }
    public int ThirtyDayCount { get; set; }
    public decimal SixtyDayAmount { get; set; }
    public int SixtyDayCount { get; set; }
    public decimal NinetyPlusDayAmount { get; set; }
    public int NinetyPlusDayCount { get; set; }
    public decimal TotalOutstanding { get; set; }
    public int TotalCount { get; set; }
}
