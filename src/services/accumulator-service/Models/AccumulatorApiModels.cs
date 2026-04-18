namespace AccumulatorService.Models;

/// <summary>
/// Projection shape returned by GET /api/v1/accumulators/{memberId} and the
/// member-service compat route. Mirrors MemberAccumulatorsResponse in member-service
/// (and MemberAccumulators in the portal) so the proxy is a pass-through, not a
/// re-shape. Typed lists intentionally — <c>List&lt;object&gt;</c> was a smell that
/// kept the portal blind to the real shape.
/// </summary>
public class AccumulatorResponse
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }

    public List<ServiceAccumulatorDto> ServiceAccumulators { get; set; } = new();
    public List<AccumulatorActivityDto> RecentActivity { get; set; } = new();
}

public class ServiceAccumulatorDto
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal Used { get; set; }
    public decimal Limit { get; set; }
    public string Unit { get; set; } = "USD";
}

public class AccumulatorActivityDto
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public DateTime OccurredAt { get; set; }
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }
    public string? Reason { get; set; }
    public string ActorId { get; set; } = "system";
}

public class AccumulatorHistoryResponse
{
    public string MemberId { get; set; } = string.Empty;
    public List<AccumulatorSnapshotSummary> Snapshots { get; set; } = new();
    public List<AccumulatorActivityDto> Events { get; set; } = new();
}

public class AccumulatorSnapshotSummary
{
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }
    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public long Version { get; set; }
    public DateTime LastUpdatedDate { get; set; }
}
