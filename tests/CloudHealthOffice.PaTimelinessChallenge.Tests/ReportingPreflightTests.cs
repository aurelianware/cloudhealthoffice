using AuthorizationService.Models;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>
/// In-process preflight: CHO's CMS-0057-F metrics over the imported history
/// cohort, compared with the answer key. The deployed-stack run repeats this
/// against the real endpoints; this catches a disagreement on every PR.
/// </summary>
public sealed class ReportingPreflightTests
{
    [Fact]
    public async Task Cho_metrics_match_the_answer_key_for_the_history_cohort()
    {
        var store = await ImportContractTests.ImportAsync();
        foreach (var f in PaCorpusWriter.ReportingFacts(CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.History)))
        {
            var a = store.Values.Single(x => x.AuthorizationNumber == f.AuthorizationNumber);
            a.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(a, ImportContractTests.Command(f),
                "pa-timeliness-challenge", CorpusFixture.AsOf.UtcDateTime));
        }

        var history = CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.History).ToList();
        var from = history.Min(c => c.ReceivedAt).UtcDateTime;
        var to = history.Max(c => c.ReceivedAt).UtcDateTime;
        var diffs = new List<string>();
        Compare("all", PaExpectedMetrics.Compute("all", history),
            Cms0057Reporting.ComputeMetrics(store.Values, from, to, lineOfBusiness: null), diffs);
        foreach (var lob in Enum.GetValues<PaLineOfBusiness>())
            Compare(lob.ToString(), PaExpectedMetrics.Compute(lob.ToString(), history.Where(c => c.LineOfBusiness == lob)),
                Cms0057Reporting.ComputeMetrics(store.Values, from, to, Enum.Parse<LineOfBusiness>(lob.ToString())), diffs);
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }

    private static void Compare(string scope, PaExpectedMetrics expected, Cms0057Metrics actual, List<string> diffs)
    {
        void Check(string name, object? want, object? got)
        {
            if (!Same(want, got)) diffs.Add($"{scope} {name}: expected {want}, CHO {got}");
        }
        Check("TotalRequests", expected.TotalRequests, actual.TotalRequests);
        Check("OpenRequests", expected.OpenRequests, actual.OpenRequests);
        Check("DecidedRequests", expected.DecidedRequests, actual.DecidedRequests);
        Check("ExtendedThenApprovedPercent", expected.ExtendedThenApprovedPercent, actual.ExtendedThenApprovedPercent);
        foreach (var (name, want, got) in new[] { ("Standard", expected.Standard, actual.Standard), ("Expedited", expected.Expedited, actual.Expedited) })
        {
            Check($"{name}.Requests", want.Requests, got.Requests);
            Check($"{name}.Decided", want.Decided, got.Decided);
            Check($"{name}.Approved", want.Approved, got.Approved);
            Check($"{name}.Denied", want.Denied, got.Denied);
            Check($"{name}.ApprovedPercent", want.ApprovedPercent, got.ApprovedPercent);
            Check($"{name}.DeniedPercent", want.DeniedPercent, got.DeniedPercent);
            Check($"{name}.AverageDecisionHours", want.AverageDecisionHours, got.AverageDecisionHours);
            Check($"{name}.MedianDecisionHours", want.MedianDecisionHours, got.MedianDecisionHours);
            Check($"{name}.DecidedWithinTimeframe", want.DecidedWithinTimeframe, got.DecidedWithinTimeframe);
            Check($"{name}.DecidedWithinTimeframePercent", want.DecidedWithinTimeframePercent, got.DecidedWithinTimeframePercent);
            Check($"{name}.Extended", want.Extended, got.Extended);
            Check($"{name}.ExtendedThenApproved", want.ExtendedThenApproved, got.ExtendedThenApproved);
            Check($"{name}.ApprovedAfterAppeal", want.ApprovedAfterAppeal, got.ApprovedAfterAppeal);
        }
    }

    // Rounded values (one decimal) may differ by one step at an exact midpoint:
    // the key rounds half away from zero, CHO rounds half to even. Counts must match exactly.
    private static bool Same(object? want, object? got) => want is double w && got is double g
        ? Math.Abs(w - g) <= 0.1 + 1e-9
        : Equals(want, got);
}
