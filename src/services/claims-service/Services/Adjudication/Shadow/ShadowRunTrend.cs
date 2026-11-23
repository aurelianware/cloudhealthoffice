using System.Globalization;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// How shadow runs moved over time, one row per day or week: how much of the
/// volume CHO decided, how often it agreed with the core, and the dollars on
/// each side. Built from the runs' stored totals, so it needs no results. A
/// plan on the nightly file drop reads it to watch the match rate climb as gaps
/// close, and to see whether the core's possible over- and underpayments are
/// steady or growing.
/// </summary>
public class ShadowRunTrend
{
    /// <summary>"day" or "week" (weeks start on Monday, UTC).</summary>
    public string Bucket { get; init; } = ShadowRunTrendBuilder.Week;

    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }

    /// <summary>Oldest first. Periods with no completed run are left out.</summary>
    public List<ShadowRunTrendPoint> Points { get; init; } = [];
}

public class ShadowRunTrendPoint
{
    /// <summary>The day, or the Monday that starts the week (yyyy-MM-dd).</summary>
    public string Period { get; set; } = string.Empty;

    public int Runs { get; set; }
    public int ClaimsCompared { get; set; }

    /// <summary>Claims whose core answer was final.</summary>
    public int ComparableClaims { get; set; }

    public int DecidedClaims { get; set; }
    public int MatchedClaims { get; set; }

    /// <summary>Decided of comparable (0 to 1); null when none were comparable.</summary>
    public decimal? DecidedRate { get; set; }

    /// <summary>Matched of decided (0 to 1); null when CHO decided none.</summary>
    public decimal? MatchRate { get; set; }

    /// <summary>Dollars on claims where the core paid more than CHO would have: possible overpayments.</summary>
    public decimal CorePaidMoreAmount { get; set; }

    /// <summary>Dollars on claims where the core paid less: possible underpayments.</summary>
    public decimal CorePaidLessAmount { get; set; }
}

public static class ShadowRunTrendBuilder
{
    public const string Day = "day";
    public const string Week = "week";

    /// <summary>
    /// Rolls up the completed runs that started in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).
    /// When the same import batch was run more than once, only its latest
    /// completed run counts, so a re-run does not count a file twice.
    /// </summary>
    public static ShadowRunTrend Build(
        IEnumerable<ShadowAdjudicationRun> runs, DateTime fromUtc, DateTime toUtc, string bucket)
    {
        ArgumentNullException.ThrowIfNull(runs);
        bucket = string.Equals(bucket, Day, StringComparison.OrdinalIgnoreCase) ? Day : Week;

        var counted = runs
            .Where(r => r.Status == ShadowRunStatus.Completed && r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ImportBatchId) ? $"run:{r.Id}" : $"batch:{r.ImportBatchId}", StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).First());

        var points = counted
            .GroupBy(r => PeriodStart(r.StartedAtUtc, bucket))
            .OrderBy(g => g.Key)
            .Select(g => Point(g.Key, g.ToList()))
            .ToList();

        return new ShadowRunTrend { Bucket = bucket, FromUtc = fromUtc, ToUtc = toUtc, Points = points };
    }

    internal static DateOnly PeriodStart(DateTime utc, string bucket)
    {
        var day = DateOnly.FromDateTime(utc);
        if (bucket == Day) return day;
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        return day.AddDays(-offset);
    }

    private static ShadowRunTrendPoint Point(DateOnly period, IReadOnlyList<ShadowAdjudicationRun> runs)
    {
        int Count(ShadowAdjudicationRun r, string kind) => r.CountsByKind.TryGetValue(kind, out var n) ? n : 0;

        var compared = runs.Sum(r => r.ClaimsCompared);
        var comparable = runs.Sum(r => r.ClaimsCompared - Count(r, ShadowDiscrepancyKind.NotComparable));
        var decided = runs.Sum(r => r.ClaimsCompared - Count(r, ShadowDiscrepancyKind.NotComparable) - Count(r, ShadowDiscrepancyKind.ChoUndecided));
        var matched = runs.Sum(r => Count(r, ShadowDiscrepancyKind.Match));

        // Over the decided claims, net = Σv and absolute = Σ|v| with v = CHO paid − core paid,
        // so the claims where the core paid more (v < 0) total (absolute − net) / 2 and the
        // claims where it paid less (v > 0) total (absolute + net) / 2.
        var net = runs.Sum(r => r.NetPaidVariance);
        var absolute = runs.Sum(r => r.AbsolutePaidVariance);

        return new ShadowRunTrendPoint
        {
            Period = period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Runs = runs.Count,
            ClaimsCompared = compared,
            ComparableClaims = comparable,
            DecidedClaims = decided,
            MatchedClaims = matched,
            DecidedRate = comparable > 0 ? Math.Round((decimal)decided / comparable, 4) : null,
            MatchRate = decided > 0 ? Math.Round((decimal)matched / decided, 4) : null,
            CorePaidMoreAmount = Math.Max(0m, (absolute - net) / 2m),
            CorePaidLessAmount = Math.Max(0m, (absolute + net) / 2m),
        };
    }
}
