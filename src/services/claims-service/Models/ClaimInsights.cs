namespace ClaimsService.Models;

/// <summary>
/// Claim filters shared by the claims list (POST /api/claims/search) and
/// Claims Insights. Each Insights figure is a count of the claims matching
/// one of these, and its drill-down link carries the same criteria, so the
/// list it opens contains exactly the claims the figure counted
/// (portal-navigation.md §5.1, rules 1 and 2).
/// Only current claim versions count: a version superseded by an adjustment
/// is never counted or listed.
/// </summary>
public sealed class ClaimCriteria
{
    public string? ClaimNumber { get; init; }

    /// <summary>A prior authorization number on the claim header or any line (837 REF*G1).</summary>
    public string? AuthorizationNumber { get; init; }

    public string? MemberId { get; init; }

    /// <summary>Billing or rendering NPI.</summary>
    public string? ProviderNpi { get; init; }

    public DateTime? ServiceDateFrom { get; init; }
    public DateTime? ServiceDateTo { get; init; }

    /// <summary>One exact status.</summary>
    public ClaimStatus? Status { get; init; }

    /// <summary>Open inventory: not yet adjudicated (<see cref="ClaimStatusSets.Open"/>).</summary>
    public bool Open { get; init; }

    /// <summary>Adjudicated to an outcome (<see cref="ClaimStatusSets.Outcome"/>).</summary>
    public bool Adjudicated { get; init; }

    /// <summary>Received (CHO intake, <see cref="Claim.SubmittedDate"/>) strictly after this instant.</summary>
    public DateTime? ReceivedAfter { get; init; }

    /// <summary>Received at or before this instant.</summary>
    public DateTime? ReceivedOnOrBefore { get; init; }

    /// <summary>Adjudicated at or after this instant.</summary>
    public DateTime? AdjudicatedFrom { get; init; }

    /// <summary>Adjudicated before this instant.</summary>
    public DateTime? AdjudicatedBefore { get; init; }

    /// <summary>Pended with this pend code (implies <see cref="ClaimStatus.Pended"/>).</summary>
    public string? PendCode { get; init; }

    /// <summary>Denied with this claim-level reason code (implies a denied status).</summary>
    public string? DenialReasonCode { get; init; }

    /// <summary>Reached its outcome with no examiner resolution.</summary>
    public bool? AutoAdjudicated { get; init; }

    public LineOfBusiness? LineOfBusiness { get; init; }

    /// <summary>The pend code that groups pended claims with no pend details.</summary>
    public const string UnspecifiedPendCode = "UNSPECIFIED";

    /// <summary>The denial code that groups denied claims with no claim-level reason.</summary>
    public const string UnspecifiedDenialCode = "UNSPECIFIED";
}

public static class ClaimStatusSets
{
    /// <summary>Not yet adjudicated: the open inventory.</summary>
    public static readonly ClaimStatus[] Open =
        [ClaimStatus.Submitted, ClaimStatus.Received, ClaimStatus.InAdjudication, ClaimStatus.Pended];

    /// <summary>Adjudicated to pay or deny, finalized or not (voids excluded).</summary>
    public static readonly ClaimStatus[] Outcome =
        [ClaimStatus.Approved, ClaimStatus.Deny, ClaimStatus.Paid, ClaimStatus.Denied, ClaimStatus.PartiallyPaid];

    public static readonly ClaimStatus[] Denied = [ClaimStatus.Deny, ClaimStatus.Denied];
}

/// <summary>Claims Insights for a period (GET /api/claims/insights).</summary>
public sealed class ClaimInsights
{
    public DateTime AsOf { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public LineOfBusiness? LineOfBusiness { get; init; }

    /// <summary>Open inventory now, by status and by age since receipt.</summary>
    public ClaimInventoryInsight Inventory { get; init; } = new();

    /// <summary>Pended claims now, by pend code, most first.</summary>
    public List<ClaimPendReasonInsight> PendReasons { get; init; } = new();

    /// <summary>Claims adjudicated in the period.</summary>
    public ClaimOutcomeInsight Outcomes { get; init; } = new();

    /// <summary>Claims denied in the period, by claim-level reason code (top 10).</summary>
    public List<ClaimDenialReasonInsight> DenialReasons { get; init; } = new();

    /// <summary>Receipt to adjudication, for claims adjudicated in the period.</summary>
    public ClaimTurnaroundInsight Turnaround { get; init; } = new();
}

public sealed class ClaimInventoryInsight
{
    public int Total { get; init; }
    public decimal TotalCharge { get; init; }
    public List<ClaimStatusCount> ByStatus { get; init; } = new();
    public List<ClaimAgingBucket> Aging { get; init; } = new();
}

public sealed class ClaimStatusCount
{
    public ClaimStatus Status { get; init; }
    public int Count { get; init; }
    public decimal Charge { get; init; }
}

/// <summary>Open claims aged <see cref="MinDays"/>–<see cref="MaxDays"/> full days since receipt (null max: and older).</summary>
public sealed class ClaimAgingBucket
{
    public string Label { get; init; } = string.Empty;
    public int MinDays { get; init; }
    public int? MaxDays { get; init; }
    public int Count { get; init; }
    public decimal Charge { get; init; }

    /// <summary>The drill-down criteria: received after this instant (null: no lower bound).</summary>
    public DateTime? ReceivedAfter { get; init; }

    /// <summary>The drill-down criteria: received at or before this instant.</summary>
    public DateTime? ReceivedOnOrBefore { get; init; }
}

public sealed class ClaimPendReasonInsight
{
    public string PendCode { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime? OldestPendedAt { get; init; }
}

public sealed class ClaimOutcomeInsight
{
    public int Adjudicated { get; init; }
    public int AutoAdjudicated { get; init; }

    /// <summary>Share of adjudicated claims reaching their outcome with no examiner resolution (0–1); null when none adjudicated.</summary>
    public decimal? AutoAdjudicationRate { get; init; }

    public List<ClaimStatusCount> ByStatus { get; init; } = new();
    public decimal TotalCharge { get; init; }
    public decimal TotalPaid { get; init; }
}

public sealed class ClaimDenialReasonInsight
{
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Count { get; init; }
}

public sealed class ClaimTurnaroundInsight
{
    /// <summary>Claims measured (adjudicated in the period, capped at <see cref="SampleLimit"/>).</summary>
    public int Count { get; init; }
    public decimal? AverageDays { get; init; }
    public decimal? MedianDays { get; init; }
    public decimal? P90Days { get; init; }

    /// <summary>True when more claims were adjudicated than were measured.</summary>
    public bool Sampled { get; init; }

    public const int SampleLimit = 100_000;
}

public sealed class AuthorizationUsageRequest
{
    public const int MaxNumbers = 2000;
    public List<string> AuthorizationNumbers { get; init; } = new();
}

/// <summary>A prior authorization number and how many current claims carry it.</summary>
public sealed record AuthorizationUsage(string AuthorizationNumber, int ClaimCount);
