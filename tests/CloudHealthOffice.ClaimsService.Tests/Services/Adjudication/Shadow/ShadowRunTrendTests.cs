using ClaimsService.Services.Adjudication.Shadow;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowRunTrendTests
{
    private static readonly DateTime From = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Runs_RollUpByWeek_StartingMonday()
    {
        var runs = new[]
        {
            // Tuesday Nov 3 and Thursday Nov 5: the week of Monday Nov 2.
            Run("r1", "b1", new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc), compared: 100, notComparable: 10, undecided: 30, match: 40, net: -50m, abs: 70m),
            Run("r2", "b2", new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc), compared: 50, notComparable: 0, undecided: 10, match: 30, net: 20m, abs: 20m),
            // Monday Nov 9.
            Run("r3", "b3", new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc), compared: 10, notComparable: 0, undecided: 0, match: 10, net: 0m, abs: 0m),
        };

        var trend = ShadowRunTrendBuilder.Build(runs, From, To, "week");

        Assert.Equal("week", trend.Bucket);
        Assert.Equal(["2026-11-02", "2026-11-09"], trend.Points.Select(p => p.Period));
        var first = trend.Points[0];
        Assert.Equal(2, first.Runs);
        Assert.Equal(150, first.ClaimsCompared);
        Assert.Equal(140, first.ComparableClaims);
        Assert.Equal(100, first.DecidedClaims);
        Assert.Equal(70, first.MatchedClaims);
        Assert.Equal(0.7143m, first.DecidedRate);
        Assert.Equal(0.7m, first.MatchRate);
        // net −30, absolute 90: core paid more on 60, less on 30.
        Assert.Equal(60m, first.CorePaidMoreAmount);
        Assert.Equal(30m, first.CorePaidLessAmount);
    }

    [Fact]
    public void ABatchRunTwice_CountsItsLatestRunOnly_AndUnfinishedOrOutOfRangeRunsAreLeftOut()
    {
        var runs = new[]
        {
            Run("old", "b1", new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc), compared: 100, notComparable: 0, undecided: 80, match: 10, net: 0m, abs: 0m),
            Run("new", "b1", new DateTime(2026, 11, 4, 6, 0, 0, DateTimeKind.Utc), compared: 100, notComparable: 0, undecided: 20, match: 70, net: 0m, abs: 0m),
            Run("running", "b2", new DateTime(2026, 11, 4, 7, 0, 0, DateTimeKind.Utc), compared: 5, notComparable: 0, undecided: 0, match: 5, net: 0m, abs: 0m, status: ShadowRunStatus.Running),
            Run("before", "b3", new DateTime(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc), compared: 5, notComparable: 0, undecided: 0, match: 5, net: 0m, abs: 0m),
        };

        var trend = ShadowRunTrendBuilder.Build(runs, From, To, "day");

        var point = Assert.Single(trend.Points);
        Assert.Equal("2026-11-04", point.Period);
        Assert.Equal(1, point.Runs);
        Assert.Equal(80, point.DecidedClaims);
    }

    [Fact]
    public void RunsWithNoComparableClaims_HaveNoRates()
    {
        var runs = new[] { Run("r1", null, new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc), compared: 4, notComparable: 4, undecided: 0, match: 0, net: 0m, abs: 0m) };

        var point = Assert.Single(ShadowRunTrendBuilder.Build(runs, From, To, "unknown").Points);

        Assert.Null(point.DecidedRate);
        Assert.Null(point.MatchRate);
    }

    private static ShadowAdjudicationRun Run(
        string id, string? batch, DateTime started, int compared, int notComparable, int undecided, int match,
        decimal net, decimal abs, string status = ShadowRunStatus.Completed) => new()
    {
        Id = id,
        ImportBatchId = batch,
        Status = status,
        StartedAtUtc = started,
        ClaimsCompared = compared,
        CountsByKind =
        {
            [ShadowDiscrepancyKind.NotComparable] = notComparable,
            [ShadowDiscrepancyKind.ChoUndecided] = undecided,
            [ShadowDiscrepancyKind.Match] = match,
        },
        NetPaidVariance = net,
        AbsolutePaidVariance = abs,
    };
}
