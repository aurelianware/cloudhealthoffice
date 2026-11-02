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

/// <summary>In-process preflight for the annual public-reporting draft (final outcomes, bitemporal ledger).</summary>
public sealed class AnnualDraftPreflightTests
{
    [Fact]
    public async Task Cho_annual_draft_matches_the_answer_key_for_every_line_of_business()
    {
        var store = await ImportContractTests.ImportAsync();
        var history = CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.History).ToList();
        foreach (var f in PaCorpusWriter.ReportingFacts(history))
        {
            var a = store.Values.Single(x => x.AuthorizationNumber == f.AuthorizationNumber);
            a.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(a, ImportContractTests.Command(f),
                "pa-timeliness-challenge", CorpusFixture.AsOf.UtcDateTime));
        }

        var year = history[0].ReceivedAt.UtcDateTime.Year;
        Assert.All(history, c => Assert.Equal(year, c.ReceivedAt.UtcDateTime.Year));
        var diffs = new List<string>();
        foreach (var lob in Enum.GetValues<PaLineOfBusiness>())
        {
            var report = new AnnualPaReporting(year, "UTC", Enum.Parse<LineOfBusiness>(lob.ToString()),
                CorpusFixture.AsOf.UtcDateTime, CorpusFixture.AsOf.UtcDateTime);
            foreach (var a in store.Values) report.Add(a);
            var actual = report.Finish();
            var expected = PaExpectedAnnual.Compute(history, lob, year);
            Assert.Equal(0, actual.LegacyRecordsWithoutEvidence);
            foreach (var (name, want, got) in new[] { ("Standard", expected.Standard, actual.Standard), ("Expedited", expected.Expedited, actual.Expedited) })
            {
                void Check(string field, object? w, object? g)
                {
                    var same = w is double wd && g is double gd ? Math.Abs(wd - gd) <= 0.0001 + 1e-9 : Equals(w, g);
                    if (!same) diffs.Add($"{lob} {name}.{field}: expected {w}, CHO {g}");
                }
                Check("Requests", want.Requests, got.Requests);
                Check("Approved", want.Approved, got.Approved);
                Check("Denied", want.Denied, got.Denied);
                Check("Undecided", want.Undecided, got.Undecided);
                Check("ApprovedAfterAppeal", want.ApprovedAfterAppeal, got.ApprovedAfterAppeal);
                Check("ExtendedThenApproved", want.ExtendedThenApproved, got.ExtendedThenApproved);
                Check("ApprovedPercent", want.ApprovedPercent, got.ApprovedPercent);
                Check("DeniedPercent", want.DeniedPercent, got.DeniedPercent);
                Check("ApprovedAfterAppealPercent", want.ApprovedAfterAppealPercent, got.ApprovedAfterAppealPercent);
                Check("AverageInitialDecisionHours", want.AverageInitialDecisionHours, got.AverageInitialDecisionHours);
                Check("MedianInitialDecisionHours", want.MedianInitialDecisionHours, got.MedianInitialDecisionHours);
                Check("InitialDecisionTimingUnavailable", 0, got.InitialDecisionTimingUnavailable);
                Check("ConflictingFinalOutcomes", 0, got.ConflictingFinalOutcomes);
            }
        }
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }
}
