namespace CloudHealthOffice.Events;

/// <summary>
/// Event payload for claims.finalized.v1. Emitted by claims-service when a claim
/// reaches a terminal adjudication state (paid, denied, or reversed). Consumed by
/// accumulator-service to update member deductible/OOP counters and by downstream
/// analytics/risk consumers.
///
/// This is an INTERNAL CHO event, not a FHIR ExplanationOfBenefit. EOB is a
/// query-time projection exposed by claims-service for Patient Access / Payer-to-Payer
/// surfaces (Phase 3); coupling this event to the FHIR spec's versioning cadence would
/// be a footgun. Carry only what accumulator aggregation needs, flat.
/// </summary>
public class ClaimFinalizedEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "claim.finalized";
    public int EventSchemaVersion { get; set; } = 1;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Plan year boundaries that govern which AccumulatorSnapshot to update.</summary>
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    /// <summary>Service date drives plan-year selection for retro claims.</summary>
    public DateTime ServiceDate { get; set; }

    /// <summary>When adjudication decided the amounts; distinct from ServiceDate.</summary>
    public DateTimeOffset AdjudicationTimestamp { get; set; }

    /// <summary>Terminal status: Paid | Denied | Reversed.</summary>
    public string FinalStatus { get; set; } = "Paid";

    /// <summary>
    /// Primary benefit category for the whole claim (e.g. "PrimaryCare", "Lab", "ER").
    /// Line-level categories live on <see cref="LineItems"/>; this is a convenience
    /// rollup for single-category claims.
    /// </summary>
    public string BenefitCategory { get; set; } = string.Empty;

    /// <summary>True when the amounts below contribute to the family aggregate in addition to individual.</summary>
    public bool IsFamilyAggregate { get; set; }

    // Claim-level applied amounts. For multi-line claims these are the sums of
    // LineItems[*]. Consumers MAY prefer LineItems for category-aware aggregation
    // and MUST use claim-level values when LineItems is empty.
    public decimal DeductibleApplied { get; set; }
    public decimal CoinsuranceApplied { get; set; }
    public decimal CopayApplied { get; set; }
    public decimal OopApplied { get; set; }
    public decimal PlanPaid { get; set; }
    public decimal MemberResponsibility { get; set; }

    /// <summary>
    /// Per-line applied amounts. Populated when a single claim spans multiple
    /// benefit categories (e.g. PCP visit + lab draw). Most claims have one line.
    /// </summary>
    public List<ClaimFinalizedLineItem> LineItems { get; set; } = new();
}

public class ClaimFinalizedLineItem
{
    public int LineNumber { get; set; }
    public string BenefitCategory { get; set; } = string.Empty;
    public string ServiceCode { get; set; } = string.Empty;
    public decimal DeductibleApplied { get; set; }
    public decimal CoinsuranceApplied { get; set; }
    public decimal CopayApplied { get; set; }
    public decimal OopApplied { get; set; }
    public decimal PlanPaid { get; set; }
    public decimal MemberResponsibility { get; set; }
}
