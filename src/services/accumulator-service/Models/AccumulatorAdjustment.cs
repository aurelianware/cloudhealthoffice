using System.ComponentModel.DataAnnotations;

namespace AccumulatorService.Models;

/// <summary>
/// Request body for POST /api/v1/accumulators/{memberId}/adjust. Manual overrides
/// by an authorized operator (e.g., to reflect an out-of-system payment or correct
/// a miskeyed claim). Every adjustment produces an AccumulatorEvent with EventType
/// = ManualAdjustment and an AccumulatorAdjustedEvent on the bus.
/// </summary>
public class AccumulatorAdjustmentRequest
{
    [Required]
    public DateTime PlanYearStart { get; set; }

    [Required]
    public DateTime PlanYearEnd { get; set; }

    /// <summary>Operator performing the adjustment. Required for audit.</summary>
    [Required]
    [StringLength(200)]
    public string ActorId { get; set; } = string.Empty;

    /// <summary>Free-text justification. Required for audit.</summary>
    [Required]
    [StringLength(2000, MinimumLength = 4)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Signed deltas. Negative values reduce counters (e.g. reversing a posted claim).</summary>
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }

    public List<ServiceAccumulatorAdjustment> ServiceDeltas { get; set; } = new();

    /// <summary>Optional. When provided, the adjustment is idempotent against this key.</summary>
    [StringLength(200)]
    public string? AdjustmentId { get; set; }
}

public class ServiceAccumulatorAdjustment
{
    [Required]
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal UsedDelta { get; set; }
    public string Unit { get; set; } = "USD";
}

public class AccumulatorAdjustmentResponse
{
    public string AdjustmentId { get; set; } = string.Empty;
    public AccumulatorSnapshot Snapshot { get; set; } = new();
}
