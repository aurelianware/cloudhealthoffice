namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// The annual public-reporting draft a correct implementation produces for one
/// line of business and calendar year (the shape of CHO's
/// <c>GET api/authorizations/cms-0057/annual-draft</c>). It differs from the
/// operational metrics in one way the rule makes explicit: a request's approved
/// or denied count follows its <em>final</em> outcome (an approval on appeal
/// counts as approved, and also as approved after appeal), while decision time
/// stays receipt to <em>initial</em> determination. Percentages are of all
/// requests in the cohort. Values are rounded to four decimal places, half away from zero.
/// </summary>
public sealed record PaExpectedAnnual
{
    public required PaLineOfBusiness LineOfBusiness { get; init; }
    public required int Year { get; init; }
    public required PaExpectedAnnualGroup Standard { get; init; }
    public required PaExpectedAnnualGroup Expedited { get; init; }
    public double? ExtendedThenApprovedPercent { get; init; }

    /// <summary>
    /// The draft for the cases received in <paramref name="year"/> (UTC) on
    /// <paramref name="lineOfBusiness"/>, using each case's scripted decision time.
    /// </summary>
    public static PaExpectedAnnual Compute(IEnumerable<PaCase> cases, PaLineOfBusiness lineOfBusiness, int year,
        Func<PaCase, double?>? observedHours = null)
    {
        var cohort = cases.Where(c => c.Expected.InScope && c.LineOfBusiness == lineOfBusiness
            && c.ReceivedAt.UtcDateTime.Year == year).ToList();
        double? Hours(PaCase c) => c.Expected.DecisionHours ?? observedHours?.Invoke(c);
        var standard = Group(cohort.Where(c => !c.Expected.Expedited).ToList(), Hours);
        var expedited = Group(cohort.Where(c => c.Expected.Expedited).ToList(), Hours);
        return new PaExpectedAnnual
        {
            LineOfBusiness = lineOfBusiness,
            Year = year,
            Standard = standard,
            Expedited = expedited,
            ExtendedThenApprovedPercent = Percent(standard.ExtendedThenApproved + expedited.ExtendedThenApproved,
                standard.Requests + expedited.Requests),
        };
    }

    private static PaExpectedAnnualGroup Group(List<PaCase> requests, Func<PaCase, double?> hoursOf)
    {
        var approved = requests.Count(c => PaExpectedMetrics.IsApproved(c.Expected.FinalOutcome));
        var denied = requests.Count(c => c.Expected.FinalOutcome == PaOutcome.Denied);
        var appeal = requests.Count(c => c.Expected.ApprovedAfterAppeal);
        var hours = requests.Where(c => c.Expected.InitialOutcome != PaOutcome.Open)
            .Select(hoursOf).OfType<double>().OrderBy(h => h).ToList();
        return new PaExpectedAnnualGroup
        {
            Requests = requests.Count,
            Approved = approved,
            Denied = denied,
            Undecided = requests.Count(c => c.Expected.FinalOutcome == PaOutcome.Open),
            ApprovedAfterAppeal = appeal,
            ExtendedThenApproved = requests.Count(c => c.Expected.Extended && PaExpectedMetrics.IsApproved(c.Expected.FinalOutcome)),
            ApprovedPercent = Percent(approved, requests.Count),
            DeniedPercent = Percent(denied, requests.Count),
            ApprovedAfterAppealPercent = Percent(appeal, requests.Count),
            AverageInitialDecisionHours = hours.Count == 0 ? null : Round(hours.Average()),
            MedianInitialDecisionHours = hours.Count == 0 ? null
                : Round(hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2),
        };
    }

    private static double? Percent(int part, int whole) => whole == 0 ? null : Round(100.0 * part / whole);

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed record PaExpectedAnnualGroup
{
    public required int Requests { get; init; }
    public required int Approved { get; init; }
    public required int Denied { get; init; }
    public required int Undecided { get; init; }
    public required int ApprovedAfterAppeal { get; init; }
    public required int ExtendedThenApproved { get; init; }
    public double? ApprovedPercent { get; init; }
    public double? DeniedPercent { get; init; }
    public double? ApprovedAfterAppealPercent { get; init; }
    public double? AverageInitialDecisionHours { get; init; }
    public double? MedianInitialDecisionHours { get; init; }
}
