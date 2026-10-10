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
    /// Outcome of suspending the sponsor in sponsor-service when this invoice
    /// went delinquent. Null for invoices marked delinquent before this was
    /// recorded (outcome unknown). A Failed suspension is retried on every
    /// delinquency run until it succeeds.
    /// </summary>
    public SponsorSuspensionRecord? SponsorSuspension { get; set; }

    /// <summary>
    /// Where the line premiums came from: coverage-service's stored premium
    /// (the original behaviour) or the premium rating engine.
    /// </summary>
    public PricingSource PricingSource { get; set; } = PricingSource.CoveragePremium;

    /// <summary>Rated invoices: list bill (a line per subscriber) or composite (a line per tier).</summary>
    public BillFormat? BillFormat { get; set; }

    /// <summary>
    /// Rated invoices: what kept coverage off the invoice (no rate table in
    /// force, unusable enrollment data…). An invoice with exceptions stays
    /// <see cref="InvoiceStatus.Draft"/> and is never issued.
    /// </summary>
    public List<InvoiceRatingException> RatingExceptions { get; set; } = new();

    /// <summary>Rated invoices: when the lines were last computed.</summary>
    public DateTime? RatedAt { get; set; }

    /// <summary>Rated invoices: how many times the draft was computed (1 for the first).</summary>
    public int RatingRevision { get; set; }

    /// <summary>Rated invoices: when the invoice left Draft (its lines are fixed from then on).</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Rated invoices: the sponsor's billing day, so a draft can be issued without sponsor-service.</summary>
    public int? BillingDay { get; set; }

    /// <summary>
    /// Optimistic concurrency (Mongo): incremented on every save; saving a
    /// stale copy throws <see cref="Repositories.ConcurrencyConflictException"/>.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Optimistic concurrency (Cosmos); set by Cosmos DB.</summary>
    [JsonPropertyName("_etag")]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    public string? ETag { get; set; }

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
        // A composite (per-tier) line covers several subscribers: count them from its components.
        MemberCount = LineItems
            .SelectMany(li => li.Components is { Count: > 0 } c ? c.Select(x => x.MemberId) : new[] { li.MemberId })
            .Distinct().Count();
    }
}

public enum PricingSource
{
    /// <summary>Line premium = coverage-service's monthlyPremium + employerContribution (original behaviour).</summary>
    CoveragePremium = 0,

    /// <summary>Line premium computed by the premium rating engine from the plan's rate table.</summary>
    RatingEngine = 1
}

public enum BillFormat
{
    /// <summary>One line per subscriber coverage (and rate version).</summary>
    ListBill,

    /// <summary>
    /// One line per plan, tier and rate for full-month coverage (quantity ×
    /// unit rate); prorated coverage-months keep their own lines.
    /// </summary>
    Composite
}

/// <summary>Why coverage could not be put on a rated invoice.</summary>
public class InvoiceRatingException
{
    /// <summary>RATE_NOT_FOUND, RATE_TABLE_INVALID, ENROLLMENT_DATA, ZERO_CHARGE.</summary>
    [StringLength(50)]
    public string Code { get; set; } = string.Empty;

    [StringLength(50)]
    public string? CoverageId { get; set; }

    [StringLength(50)]
    public string? MemberId { get; set; }

    /// <summary>The coverage month that could not be charged or reconciled.</summary>
    public DateTime? ServiceMonth { get; set; }

    [StringLength(1000)]
    public string Message { get; set; } = string.Empty;
}

/// <summary>A run of days on a rated line with one household rating.</summary>
public class InvoiceRatingSegment
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Days { get; set; }

    /// <summary>EMP, ESP, ECH, FAM for the household rated in this segment.</summary>
    [StringLength(3)]
    public string? CoverageLevel { get; set; }

    public int RatedMembers { get; set; }

    /// <summary>The household's monthly premium under the line's rate version.</summary>
    public decimal MonthlyPremium { get; set; }

    /// <summary>Charged for the segment, rounded once (see the rounding rules of PremiumInvoiceCalculator).</summary>
    public decimal Amount { get; set; }

    /// <summary>How the amount was reached, e.g. "500.00 × 22/31" or "full month".</summary>
    [StringLength(200)]
    public string? Basis { get; set; }
}

/// <summary>One coverage billed on a composite (per-tier) line.</summary>
public class InvoiceLineComponent
{
    [StringLength(50)]
    public string CoverageId { get; set; } = string.Empty;

    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? MemberName { get; set; }

    public decimal Amount { get; set; }
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

    // ── Rated lines (PricingSource.RatingEngine) ──────────────────────

    /// <summary>Rate table the line was rated with; with <see cref="RateTableVersion"/> it reproduces the line.</summary>
    [StringLength(100)]
    public string? RateTableId { get; set; }

    /// <summary>Version of <see cref="RateTableId"/> used (stored versions never change).</summary>
    public int? RateTableVersion { get; set; }

    /// <summary>Content hash of that rate table version as stored.</summary>
    [StringLength(100)]
    public string? RateTableHash { get; set; }

    /// <summary>Tier, AgeBand or Composite.</summary>
    [StringLength(20)]
    public string? RatingMethod { get; set; }

    /// <summary>Daily, HalfMonth or FullMonth.</summary>
    [StringLength(20)]
    public string? ProrationRule { get; set; }

    /// <summary>First day the line charges for.</summary>
    public DateTime? ServicePeriodStart { get; set; }

    /// <summary>Last day the line charges for.</summary>
    public DateTime? ServicePeriodEnd { get; set; }

    /// <summary>How the line amount was built; the line is the sum of its segments.</summary>
    public List<InvoiceRatingSegment>? RatingSegments { get; set; }

    /// <summary>Composite lines: how many subscriber coverages the line bills.</summary>
    public int? Quantity { get; set; }

    /// <summary>Composite lines: the monthly amount per coverage (TotalPremium = Quantity × UnitRate).</summary>
    public decimal? UnitRate { get; set; }

    /// <summary>Composite lines: each coverage billed and its amount (they sum to the line).</summary>
    public List<InvoiceLineComponent>? Components { get; set; }
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

    /// <summary>
    /// Coverage the adjustment corrects (retro adds/terms). With
    /// <see cref="ServicePeriodStart"/> it is how later invoices know what was
    /// already billed for that coverage and month.
    /// </summary>
    [StringLength(50)]
    public string? CoverageId { get; set; }

    /// <summary>First day of the coverage month the adjustment corrects.</summary>
    public DateTime? ServicePeriodStart { get; set; }

    /// <summary>Last day of the coverage month the adjustment corrects.</summary>
    public DateTime? ServicePeriodEnd { get; set; }

    /// <summary>
    /// True for retro adjustments produced by the rating invoice calculator.
    /// Only these are folded into what later invoices treat as already billed.
    /// </summary>
    public bool IsRatingRetro { get; set; }

    /// <summary>Rating retro adjustments: the rate table versions (<c>id@vN</c>) the corrected charge was rated with.</summary>
    public List<string>? RateTableVersions { get; set; }
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

    /// <summary>Who recorded the payment (token subject, or "stripe-webhook").</summary>
    [StringLength(200)]
    public string? RecordedBy { get; set; }

    /// <summary>Remittance batch (820 or lockbox) the payment was applied from.</summary>
    [StringLength(100)]
    public string? RemittanceBatchId { get; set; }

    /// <summary>Item line within the remittance batch.</summary>
    public int? RemittanceLine { get; set; }

    /// <summary>Member the payment was remitted for (820 individual remittance).</summary>
    [StringLength(50)]
    public string? MemberId { get; set; }

    /// <summary>Exceptions-queue item the payment was applied from.</summary>
    [StringLength(200)]
    public string? RemittanceExceptionId { get; set; }
}

public enum SponsorSuspensionState
{
    Suspended,
    Failed
}

/// <summary>
/// What happened when premium billing asked sponsor-service to suspend the
/// sponsor of a delinquent invoice.
/// </summary>
public class SponsorSuspensionRecord
{
    public SponsorSuspensionState State { get; set; }

    public int Attempts { get; set; }

    public DateTime LastAttemptAt { get; set; }

    /// <summary>HTTP status sponsor-service answered with, when there was one.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Why the last attempt failed; null once suspended.</summary>
    [StringLength(1000)]
    public string? LastError { get; set; }

    /// <summary>Who ran the delinquency processing that made the last attempt.</summary>
    [StringLength(200)]
    public string? LastAttemptBy { get; set; }

    public DateTime? SuspendedAt { get; set; }
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
    WriteOff,

    /// <summary>
    /// Rated invoice not issued yet (exceptions to resolve, or held for
    /// review). A draft can be regenerated; it cannot be sent, paid, drafted
    /// by EFT or go overdue. Appended last so stored numeric values keep their meaning.
    /// </summary>
    Draft
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
