using System.Text.Json.Serialization;

namespace EligibilityService.Models;

/// <summary>
/// Read projection returned by GET /api/v1/eligibility/temporal.
/// Lists every coverage active on the queried service date together with the
/// COB order, plan snapshot, and a (currently stubbed) accumulator snapshot.
/// </summary>
public class TemporalEligibilityResult
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public List<TemporalCoverage> Coverages { get; set; } = new();
}

/// <summary>
/// A single coverage active on the queried service date.
/// </summary>
public class TemporalCoverage
{
    public string CoverageId { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string? PlanVersion { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>
    /// Position in the Coordination-of-Benefits stack.
    /// 1 = Primary, 2 = Secondary, 3 = Tertiary, ...
    /// </summary>
    public int CobOrder { get; set; } = 1;

    /// <summary>P / S / T — mirrors X12 SBR01.</summary>
    public string CoverageSequence { get; set; } = "P";

    public bool IsCOBRA { get; set; }
    public bool IsRetroactive { get; set; }

    public AccumulatorSnapshot? Accumulators { get; set; }
}

/// <summary>
/// Stub accumulator snapshot. Populated by IAccumulatorClient — the real
/// accumulator-service is delivered in a later prompt, so default implementation
/// returns zeros and sets Source = "stub".
/// </summary>
public class AccumulatorSnapshot
{
    public string Source { get; set; } = "stub";
    public DateTime AsOfDate { get; set; } = DateTime.UtcNow;
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }
}
