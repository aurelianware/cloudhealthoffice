namespace PremiumBillingService.StateCapitation;

/// <summary>
/// One member-month the plan expects the state to pay for: the member was
/// enrolled in the month, in this rate cell. <see cref="DaysEnrolled"/> is
/// used only when the profile prorates.
/// </summary>
public sealed record ExpectedMemberMonth(string MemberId, DateOnly Month, string RateCell, int? DaysEnrolled = null);

/// <summary>The state's PMPM for a rate cell over an effective period (inclusive; null end is open).</summary>
public sealed record StateCapitationRate(string RateCell, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal Pmpm);

/// <summary>
/// One member-month the state paid (or took back) on an 820 line. A
/// recoupment is a negative amount; a later 820 adjusting an earlier month
/// adds another line for that month.
/// </summary>
public sealed record ReceivedMemberMonth(
    string MemberId,
    DateOnly Month,
    string? RateCell,
    decimal Amount,
    string TraceNumber,
    DateTime? PaymentDate,
    IReadOnlyList<string> AdjustmentReasons);

public enum MemberMonthOutcome
{
    /// <summary>Received equals expected (within tolerance).</summary>
    PaidInFull = 1,

    /// <summary>Received less than expected.</summary>
    Underpaid = 2,

    /// <summary>Received more than expected.</summary>
    Overpaid = 3,

    /// <summary>Paid, but under a different rate cell than the plan's enrollment says.</summary>
    RateCellMismatch = 4,

    /// <summary>Expected; nothing received for the member-month.</summary>
    NotPaid = 5,

    /// <summary>Received for a member-month the plan does not expect (not enrolled, or not in this program).</summary>
    Unexpected = 6,

    /// <summary>Paid and then taken back to zero or below.</summary>
    Recouped = 7,

    /// <summary>Expected, but no state rate covers the rate cell and month, so the expected amount is unknown.</summary>
    NoRate = 8,
}

/// <summary>One member-month, expected against received.</summary>
public sealed record MemberMonthReconciliation(
    string MemberId,
    DateOnly Month,
    string? ExpectedRateCell,
    string? ReceivedRateCell,
    decimal? Expected,
    decimal Received,
    MemberMonthOutcome Outcome,
    IReadOnlyList<string> TraceNumbers,
    IReadOnlyList<string> AdjustmentReasons)
{
    /// <summary>Received minus expected (null when the expected amount is unknown).</summary>
    public decimal? Variance => Expected.HasValue ? Received - Expected.Value : null;
}

/// <summary>Totals for one month and rate cell: the line the finance team books and the state disputes.</summary>
public sealed record RateCellMonthSummary(
    DateOnly Month,
    string RateCell,
    int ExpectedMembers,
    int PaidMembers,
    decimal Expected,
    decimal Received,
    int Variances)
{
    public decimal Variance => Received - Expected;
}

/// <summary>The whole reconciliation for a set of months.</summary>
public sealed record StateCapitationReconciliation(
    string State,
    IReadOnlyList<MemberMonthReconciliation> MemberMonths,
    IReadOnlyList<RateCellMonthSummary> Summary,
    IReadOnlyList<string> Warnings)
{
    public decimal TotalExpected => Summary.Sum(s => s.Expected);
    public decimal TotalReceived => MemberMonths.Sum(m => m.Received);

    /// <summary>Member-months that need someone to look (anything but paid in full).</summary>
    public IEnumerable<MemberMonthReconciliation> Exceptions => MemberMonths.Where(m => m.Outcome != MemberMonthOutcome.PaidInFull);
}
