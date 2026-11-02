using System.Text.Json.Nodes;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

public sealed class ScorerTests
{
    private static PaCase Live(PaScenario scenario) =>
        CorpusFixture.Cases.First(c => c.Cohort == PaCohort.Live && c.Scenario == scenario);

    [Fact]
    public void A_request_a_rule_decided_on_submit_is_reported_not_scored_as_the_reviewer()
    {
        var c = Live(PaScenario.Denied);
        var o = new PaScorer.LiveObservation
        {
            CaseId = c.CaseId, AutoDecided = true, SubmitOutcome = "complete",
            Authorization = new JsonObject
            {
                ["status"] = "Approved", ["levelOfService"] = c.LevelOfService,
                ["lineOfBusiness"] = (c.LineOfBusiness == PaLineOfBusiness.Exchange ? PaLineOfBusiness.Commercial : c.LineOfBusiness).ToString(),
            },
        };
        var checks = PaScorer.ScoreLive(c, o).ToList();
        Assert.Contains(checks, k => k.Name == "reviewable" && !k.Passed);
        Assert.DoesNotContain(checks, k => k.Name == "outcome");
        // Its live metrics use what CHO recorded.
        Assert.Equal(PaOutcome.Approved, PaScorer.AsObserved(c, o)!.Expected.InitialOutcome);
    }

    [Fact]
    public void Enum_values_are_read_by_name_or_by_number()
    {
        Assert.Equal(PaOutcome.Denied, PaScorer.Outcome(new JsonObject { ["status"] = "Denied" }));
        Assert.Equal(PaOutcome.Modified, PaScorer.Outcome(new JsonObject { ["status"] = 5 }));
        Assert.Equal(PaOutcome.Open, PaScorer.Outcome(new JsonObject { ["status"] = "Pended" }));
    }

    [Fact]
    public void An_undecided_request_is_late_only_once_its_deadline_has_passed()
    {
        var c = CorpusFixture.Cases.First(c => c.Cohort == PaCohort.History && c.Scenario == PaScenario.Undecided);
        JsonNode Row(bool? within) => new JsonObject
        {
            ["authorizationNumber"] = c.AuthorizationNumber, ["withinTimeframe"] = within, ["extended"] = false,
        };
        var due = c.ReceivedAt.AddHours(c.Expected.DeadlineHours);
        Assert.Equal(0, PaScorer.ScoreTimelinessRows([c], [Row(false)], due.AddMinutes(1)).Mismatched);
        Assert.Equal(0, PaScorer.ScoreTimelinessRows([c], [Row(null)], due.AddMinutes(-1)).Mismatched);
        Assert.Equal(1, PaScorer.ScoreTimelinessRows([c], [Row(null)], due.AddMinutes(1)).Mismatched);
    }

    [Fact]
    public void A_missing_request_and_a_foreign_row_are_both_counted()
    {
        var history = CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.History && c.Expected.InScope).Take(2).ToList();
        var rows = new JsonNode[]
        {
            new JsonObject { ["authorizationNumber"] = history[0].AuthorizationNumber, ["withinTimeframe"] = history[0].Expected.DecidedWithinTimeframe ?? false, ["extended"] = history[0].Expected.Extended },
            new JsonObject { ["authorizationNumber"] = "SOMEONE-ELSE", ["withinTimeframe"] = true, ["extended"] = false },
        };
        var result = PaScorer.ScoreTimelinessRows(history, rows, CorpusFixture.AsOf);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Unexpected);
    }
}
