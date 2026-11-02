using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>The answer-key metrics, checked against a corpus small enough to count by hand.</summary>
public sealed class ExpectedMetricsTests
{
    private static PaCase Case(string id, string level, PaOutcome initial, double? hours, bool extended = false,
        bool inScope = true, bool appealApproved = false)
    {
        var deadline = (level == "U" ? 72 : 168) + (extended ? 336 : 0);
        return new PaCase
        {
            CaseId = id,
            AuthorizationNumber = id,
            Cohort = PaCohort.History,
            Scenario = PaScenario.Approved,
            LineOfBusiness = PaLineOfBusiness.Medicaid,
            LevelOfService = level,
            ReceivedAt = CorpusFixture.AsOf.AddDays(-90),
            Member = new PaMember("M", "A", "B", new DateOnly(1980, 1, 1)),
            RequestingProvider = new PaProvider("1234567893", "P"),
            ServicingProvider = new PaProvider("1234567893", "P"),
            ServiceTypeCode = "1",
            DiagnosisCodes = ["M17.11"],
            Lines = [],
            Expected = new PaExpectation
            {
                InScope = inScope,
                Expedited = level == "U",
                DeadlineHours = deadline,
                InitialOutcome = initial,
                FinalOutcome = appealApproved ? PaOutcome.Approved : initial,
                DecisionHours = hours,
                DecidedWithinTimeframe = hours is { } h ? h <= deadline : null,
                Extended = extended,
                ApprovedAfterAppeal = appealApproved,
                AdverseReasons = [],
            },
        };
    }

    [Fact]
    public void Hand_counted_corpus()
    {
        PaCase[] cases =
        [
            Case("s1", "E", PaOutcome.Approved, 10),
            Case("s2", "E", PaOutcome.Modified, 168),            // at the deadline: on time
            Case("s3", "E", PaOutcome.Denied, 200),              // late
            Case("s4", "E", PaOutcome.Approved, 300, extended: true), // inside the extension
            Case("s5", "E", PaOutcome.Open, null),               // undecided: in the denominator
            Case("s6", "E", PaOutcome.Denied, 20, appealApproved: true),
            Case("d1", "E", PaOutcome.Denied, 5, inScope: false), // drug: not counted at all
            Case("x1", "U", PaOutcome.Approved, 72),
            Case("x2", "U", PaOutcome.Denied, 73),
        ];

        var m = PaExpectedMetrics.Compute("all", cases);

        Assert.Equal(8, m.TotalRequests);
        Assert.Equal(1, m.OpenRequests);
        Assert.Equal(7, m.DecidedRequests);

        var s = m.Standard;
        Assert.Equal(6, s.Requests);
        Assert.Equal(5, s.Decided);
        Assert.Equal(3, s.Approved);           // s1, s2 (modified), s4
        Assert.Equal(2, s.Denied);             // s3, s6 (initial determination)
        Assert.Equal(50.0, s.ApprovedPercent); // 3 of 6, undecided included
        Assert.Equal(33.3, s.DeniedPercent);
        Assert.Equal(139.6, s.AverageDecisionHours); // (10+168+200+300+20)/5
        Assert.Equal(168, s.MedianDecisionHours);
        Assert.Equal(4, s.DecidedWithinTimeframe);   // all but s3
        Assert.Equal(80.0, s.DecidedWithinTimeframePercent);
        Assert.Equal(1, s.Extended);
        Assert.Equal(1, s.ExtendedThenApproved);
        Assert.Equal(1, s.ApprovedAfterAppeal);
        Assert.Equal(16.7, s.ApprovedAfterAppealPercent);

        var x = m.Expedited;
        Assert.Equal(2, x.Requests);
        Assert.Equal(1, x.DecidedWithinTimeframe);
        Assert.Equal(50.0, x.DecidedWithinTimeframePercent);
        Assert.Equal(72.5, x.MedianDecisionHours);

        Assert.Equal(12.5, m.ExtendedThenApprovedPercent); // 1 of 8
    }

    [Fact]
    public void Live_cases_count_only_once_their_observed_time_is_supplied()
    {
        var live = Case("l1", "E", PaOutcome.Approved, null) with { Cohort = PaCohort.Live };
        Assert.Null(PaExpectedMetrics.Compute("live", [live]).Standard.AverageDecisionHours);
        var observed = PaExpectedMetrics.Compute("live", [live], _ => 0.25);
        Assert.Equal(0.3, observed.Standard.AverageDecisionHours);
        Assert.Equal(1, observed.Standard.DecidedWithinTimeframe);
    }

    [Fact]
    public void Empty_scope_reports_nulls_not_zeros()
    {
        var m = PaExpectedMetrics.Compute("none", []);
        Assert.Equal(0, m.TotalRequests);
        Assert.Null(m.Standard.ApprovedPercent);
        Assert.Null(m.Standard.AverageDecisionHours);
        Assert.Null(m.ExtendedThenApprovedPercent);
    }
}
