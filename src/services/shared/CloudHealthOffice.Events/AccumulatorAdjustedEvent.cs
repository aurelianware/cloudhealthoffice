namespace CloudHealthOffice.Events;

/// <summary>
/// Event payload for accumulators.adjusted.v1. Emitted by accumulator-service when
/// a snapshot is modified — either by a finalized claim application or by a manual
/// operator override. Consumers: audit log projector, downstream analytics.
/// </summary>
public class AccumulatorAdjustedEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "accumulator.adjusted";
    public int EventSchemaVersion { get; set; } = 1;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    /// <summary>ClaimApplied | ManualAdjustment | OrphanSkipped | Reversal.</summary>
    public string AdjustmentSource { get; set; } = string.Empty;

    /// <summary>ClaimId for ClaimApplied/Reversal; AdjustmentId for ManualAdjustment.</summary>
    public string? SourceReference { get; set; }

    /// <summary>User or system principal that performed the change.</summary>
    public string ActorId { get; set; } = "system";

    /// <summary>Free-text reason required for manual adjustments.</summary>
    public string? Reason { get; set; }

    /// <summary>Signed deltas applied to each counter. Negative for reversals.</summary>
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }

    /// <summary>Per-service-category deltas, if any.</summary>
    public List<ServiceAccumulatorDelta> ServiceDeltas { get; set; } = new();
}

public class ServiceAccumulatorDelta
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal UsedDelta { get; set; }
    public string Unit { get; set; } = "USD";
}

/// <summary>
/// Event payload for accumulators.orphan.v1. Emitted when a ClaimFinalizedEvent
/// arrives with a ServiceDate that does not map to any known plan-year snapshot
/// for the member (e.g. predates earliest coverage). This is a data-quality alert,
/// not a silent drop — downstream ops tooling should surface these for review.
/// </summary>
public class OrphanAccumulatorClaimEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "accumulator.orphan";
    public int EventSchemaVersion { get; set; } = 1;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public string Reason { get; set; } = string.Empty;
}
