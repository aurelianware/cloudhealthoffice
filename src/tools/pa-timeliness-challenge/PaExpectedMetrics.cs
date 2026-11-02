namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// The CMS-0057-F prior-authorization metrics a correct implementation must
/// report for a set of cases, computed from each case's expectation alone.
/// Definitions (written from the rule, not read from CHO code):
/// <list type="bullet">
/// <item>Drug requests are out of scope and are not counted at all.</item>
/// <item>Requests are counted by receipt; undecided requests stay in the
/// denominator of the approved and denied percentages.</item>
/// <item>Approved and denied are the initial determination. Approved with
/// changes counts as approved. An approval on appeal is its own metric.</item>
/// <item>Decision time runs from receipt to the initial determination. "Within
/// timeframe" compares it, inclusively, with the request's deadline, including
/// a lawful extension.</item>
/// <item>Percentages and hours are rounded to one decimal place, half away from zero.</item>
/// </list>
/// </summary>
public sealed record PaExpectedMetrics
{
    public required string Scope { get; init; }
    public required int TotalRequests { get; init; }
    public required int OpenRequests { get; init; }
    public required int DecidedRequests { get; init; }
    public required PaExpectedGroup Standard { get; init; }
    public required PaExpectedGroup Expedited { get; init; }
    public double? ExtendedThenApprovedPercent { get; init; }

    /// <summary>
    /// The metrics for <paramref name="cases"/> whose decision time is known in
    /// advance (the history cohort). Pass <paramref name="observedHours"/> to
    /// supply measured decision times for live cases once a run has them.
    /// </summary>
    public static PaExpectedMetrics Compute(string scope, IEnumerable<PaCase> cases,
        Func<PaCase, double?>? observedHours = null)
    {
        var inScope = cases.Where(c => c.Expected.InScope).ToList();
        double? Hours(PaCase c) => c.Expected.DecisionHours ?? observedHours?.Invoke(c);
        var standard = Group(inScope.Where(c => !c.Expected.Expedited).ToList(), PaTimeframes.StandardHours, Hours);
        var expedited = Group(inScope.Where(c => c.Expected.Expedited).ToList(), PaTimeframes.ExpeditedHours, Hours);
        return new PaExpectedMetrics
        {
            Scope = scope,
            TotalRequests = inScope.Count,
            OpenRequests = inScope.Count(c => c.Expected.InitialOutcome == PaOutcome.Open),
            DecidedRequests = inScope.Count(c => c.Expected.InitialOutcome != PaOutcome.Open),
            Standard = standard,
            Expedited = expedited,
            ExtendedThenApprovedPercent = Percent(standard.ExtendedThenApproved + expedited.ExtendedThenApproved, inScope.Count),
        };
    }

    private static PaExpectedGroup Group(List<PaCase> requests, double timeframeHours, Func<PaCase, double?> hoursOf)
    {
        var decided = requests.Where(c => c.Expected.InitialOutcome != PaOutcome.Open).ToList();
        var timed = decided.Select(c => (Case: c, Hours: hoursOf(c))).Where(t => t.Hours is not null)
            .Select(t => (t.Case, Hours: t.Hours!.Value)).ToList();
        var sorted = timed.Select(t => t.Hours).OrderBy(h => h).ToList();
        var approved = decided.Count(c => IsApproved(c.Expected.InitialOutcome));
        var denied = decided.Count(c => c.Expected.InitialOutcome == PaOutcome.Denied);
        var within = timed.Count(t => t.Hours <= t.Case.Expected.DeadlineHours);
        var appealApproved = requests.Count(c => c.Expected.ApprovedAfterAppeal);
        return new PaExpectedGroup
        {
            TimeframeHours = timeframeHours,
            Requests = requests.Count,
            Decided = decided.Count,
            Approved = approved,
            Denied = denied,
            ApprovedPercent = Percent(approved, requests.Count),
            DeniedPercent = Percent(denied, requests.Count),
            AverageDecisionHours = sorted.Count == 0 ? null : Round(sorted.Average()),
            MedianDecisionHours = sorted.Count == 0 ? null : Round(Median(sorted)),
            DecidedWithinTimeframe = within,
            DecidedWithinTimeframePercent = Percent(within, sorted.Count),
            Extended = requests.Count(c => c.Expected.Extended),
            ExtendedThenApproved = requests.Count(c => c.Expected.Extended && IsApproved(c.Expected.InitialOutcome)),
            ApprovedAfterAppeal = appealApproved,
            ApprovedAfterAppealPercent = Percent(appealApproved, requests.Count),
        };
    }

    internal static bool IsApproved(PaOutcome outcome) => outcome is PaOutcome.Approved or PaOutcome.Modified;

    private static double? Percent(int part, int whole) => whole == 0 ? null : Round(100.0 * part / whole);

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

public sealed record PaExpectedGroup
{
    public required double TimeframeHours { get; init; }
    public required int Requests { get; init; }
    public required int Decided { get; init; }
    public required int Approved { get; init; }
    public required int Denied { get; init; }
    public double? ApprovedPercent { get; init; }
    public double? DeniedPercent { get; init; }
    public double? AverageDecisionHours { get; init; }
    public double? MedianDecisionHours { get; init; }
    public required int DecidedWithinTimeframe { get; init; }
    public double? DecidedWithinTimeframePercent { get; init; }
    public required int Extended { get; init; }
    public required int ExtendedThenApproved { get; init; }
    public required int ApprovedAfterAppeal { get; init; }
    public double? ApprovedAfterAppealPercent { get; init; }
}
