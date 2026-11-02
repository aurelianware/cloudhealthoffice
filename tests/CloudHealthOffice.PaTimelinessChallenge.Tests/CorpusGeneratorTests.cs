using System.Text.Json;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>The corpus is deterministic, covers every scenario, and every script obeys the timeframes it claims.</summary>
public sealed class CorpusGeneratorTests
{
    private static IReadOnlyList<PaCase> Cases => CorpusFixture.Cases;

    [Fact]
    public void Same_profile_produces_the_same_corpus()
    {
        var profile = CorpusFixture.Profile(500, seed: 11);
        var a = PaCorpusGenerator.Generate(profile);
        var b = PaCorpusGenerator.Generate(profile);
        Assert.Equal(PaCorpusWriter.HistoryCsv(a.Where(c => c.Cohort == PaCohort.History)),
            PaCorpusWriter.HistoryCsv(b.Where(c => c.Cohort == PaCohort.History)));
        Assert.Equal(JsonSerializer.Serialize(PaCorpusWriter.AnswerKey.From(profile, a), PaCorpusWriter.Json),
            JsonSerializer.Serialize(PaCorpusWriter.AnswerKey.From(profile, b), PaCorpusWriter.Json));
    }

    [Fact]
    public void A_different_seed_produces_a_different_corpus()
    {
        var a = PaCorpusGenerator.Generate(CorpusFixture.Profile(200, seed: 1));
        var b = PaCorpusGenerator.Generate(CorpusFixture.Profile(200, seed: 2));
        Assert.NotEqual(PaCorpusWriter.HistoryCsv(a.Where(c => c.Cohort == PaCohort.History)),
            PaCorpusWriter.HistoryCsv(b.Where(c => c.Cohort == PaCohort.History)));
    }

    [Fact]
    public void Count_is_exact_and_every_weighted_scenario_appears_in_its_cohort()
    {
        Assert.Equal(2_000, Cases.Count);
        Assert.Equal(100, Cases.Count(c => c.Cohort == PaCohort.Live));
        foreach (var scenario in PaCorpusProfile.DefaultHistoryWeights.Keys)
            Assert.Contains(Cases, c => c.Cohort == PaCohort.History && c.Scenario == scenario);
        foreach (var scenario in PaCorpusProfile.DefaultLiveWeights.Keys)
            Assert.Contains(Cases, c => c.Cohort == PaCohort.Live && c.Scenario == scenario);
        Assert.Equal(Cases.Count, Cases.Select(c => c.AuthorizationNumber).Distinct().Count());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(10_000)]
    public void Allocation_sums_to_the_requested_count(int count)
    {
        var plan = PaCorpusGenerator.Allocate(PaCorpusProfile.DefaultHistoryWeights, count);
        Assert.Equal(count, plan.Count);
    }

    [Fact]
    public void A_decision_exactly_at_the_deadline_is_on_time_and_one_minute_later_is_late()
    {
        foreach (var c in Cases.Where(c => c.Scenario == PaScenario.DecidedAtDeadline))
        {
            Assert.Equal(c.Expected.DeadlineHours, c.Expected.DecisionHours);
            Assert.True(c.Expected.DecidedWithinTimeframe);
        }
        foreach (var c in Cases.Where(c => c.Scenario == PaScenario.DecidedOneMinuteLate))
        {
            Assert.Equal(c.Expected.DeadlineHours + 1.0 / 60, c.Expected.DecisionHours!.Value, 6);
            Assert.False(c.Expected.DecidedWithinTimeframe);
        }
    }

    [Fact]
    public void Deadlines_are_72_or_168_hours_plus_14_days_only_when_lawfully_extended()
    {
        foreach (var c in Cases)
        {
            var baseHours = c.LevelOfService == "U" ? 72 : 168;
            Assert.Equal(baseHours + (c.Expected.Extended ? 336 : 0), c.Expected.DeadlineHours);
        }
    }

    [Fact]
    public void Lawful_extensions_are_Medicare_or_Medicaid_and_granted_before_the_original_deadline()
    {
        foreach (var c in Cases.Where(c => c.Expected.Extended))
        {
            Assert.Contains(c.LineOfBusiness, new[] { PaLineOfBusiness.Medicare, PaLineOfBusiness.Medicaid });
            Assert.NotNull(c.Extension);
            Assert.True(c.Extension!.GrantedAfter.TotalHours <= (c.LevelOfService == "U" ? 72 : 168));
            Assert.True(c.Extension.ExtendedBy <= TimeSpan.FromDays(14));
        }
        foreach (var c in Cases.Where(c => c.Expected.ExtensionMustBeRefused))
        {
            Assert.Equal(PaCohort.Live, c.Cohort);
            Assert.Contains(c.LineOfBusiness, new[] { PaLineOfBusiness.Commercial, PaLineOfBusiness.Exchange });
            Assert.False(c.Expected.Extended);
        }
    }

    [Fact]
    public void Extended_cases_land_on_the_right_side_of_the_extended_deadline()
    {
        Assert.All(Cases.Where(c => c.Cohort == PaCohort.History && c.Scenario == PaScenario.ExtendedDecidedInExtension),
            c => Assert.True(c.Expected.DecidedWithinTimeframe));
        Assert.All(Cases.Where(c => c.Scenario == PaScenario.ExtendedDecidedLate),
            c => Assert.False(c.Expected.DecidedWithinTimeframe));
        Assert.All(Cases.Where(c => c.Scenario == PaScenario.LateDecision),
            c => Assert.False(c.Expected.DecidedWithinTimeframe));
    }

    [Fact]
    public void Every_scripted_event_falls_between_receipt_and_as_of()
    {
        foreach (var c in Cases.Where(c => c.Cohort == PaCohort.History))
        {
            Assert.InRange(c.ReceivedAt, CorpusFixture.AsOf.AddYears(-1), CorpusFixture.AsOf.AddDays(-PaCorpusProfile.MinimumSettlingDays));
            foreach (var after in new[] { c.Decision?.After, c.Extension?.GrantedAfter, c.Appeal?.DecidedAfter }.OfType<TimeSpan>())
            {
                Assert.True(after >= TimeSpan.Zero);
                Assert.True(c.ReceivedAt + after <= CorpusFixture.AsOf);
            }
        }
    }

    [Fact]
    public void Adverse_outcomes_carry_a_specific_reason_for_every_denied_line()
    {
        foreach (var c in Cases.Where(c => c.Expected.InitialOutcome is PaOutcome.Denied or PaOutcome.Modified))
        {
            var denied = c.Lines.Where(l => l.Decision == PaOutcome.Denied).ToList();
            Assert.NotEmpty(denied);
            Assert.All(denied, l => Assert.False(string.IsNullOrEmpty(l.DenialReasonCode)));
            Assert.Equal(denied.Select(l => l.LineNumber), c.Expected.AdverseReasons.Select(r => r.LineNumber!.Value));
        }
        Assert.All(Cases.Where(c => c.Scenario == PaScenario.PartiallyApproved),
            c => Assert.Contains(c.Lines, l => l.Decision == PaOutcome.Approved));
    }

    [Fact]
    public void Drug_requests_are_out_of_scope_and_nothing_else_is()
    {
        Assert.All(Cases, c => Assert.Equal(c.Scenario != PaScenario.DrugExcluded, c.Expected.InScope));
    }

    [Fact]
    public void Live_cases_leave_timing_to_the_run()
    {
        Assert.All(Cases.Where(c => c.Cohort == PaCohort.Live), c =>
        {
            Assert.Null(c.Expected.DecisionHours);
            Assert.Null(c.Expected.DecidedWithinTimeframe);
            Assert.Null(c.Appeal);
        });
    }

    [Theory]
    [InlineData("123456789", 3)] // NPI 1234567893, the CMS documentation example
    public void Npi_check_digit_follows_the_Luhn_rule_with_the_80840_prefix(string body, int digit)
    {
        Assert.Equal(digit, PaCorpusGenerator.NpiCheckDigit(body));
    }

    [Fact]
    public void Profile_rejects_live_timing_scenarios_and_a_period_too_close_to_as_of()
    {
        var live = CorpusFixture.Profile() with
        {
            LiveWeights = new Dictionary<PaScenario, int> { [PaScenario.LateDecision] = 1 },
        };
        Assert.Throws<ArgumentException>(() => PaCorpusGenerator.Generate(live));
        var tooLate = CorpusFixture.Profile() with { PeriodEnd = CorpusFixture.AsOf.AddDays(-1) };
        Assert.Throws<ArgumentException>(() => PaCorpusGenerator.Generate(tooLate));
    }
}
