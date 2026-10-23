using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using FluentAssertions;
using Xunit;

namespace AppealsService.Tests.Services.RulePacks;

/// <summary>
/// The Medicare Advantage pack: the first that is federal rather than a
/// State's, and the first of a line of business other than Medicaid. What it
/// must not do is as much the point as what it does — there is no State fair
/// hearing in Part C, and the pack must not invent one.
/// </summary>
public class MedicareAdvantageRulePackTests
{
    private static readonly RulePackCatalog Catalog = RulePackCatalog.FromEmbeddedResources();
    private static readonly DateOnly AsOf = new(2026, 11, 1);

    private static RulePack Pack() =>
        Catalog.Find("TX", LineOfBusiness.Medicare, AsOf) ?? throw new InvalidOperationException("No MA pack.");

    private static DateTime Utc(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    private static RulePackEvaluation Evaluate(
        RulePackCaseType caseType, DateTime received, bool expedited = false, DateTime? decided = null) =>
        RulePackEvaluator.Evaluate(Pack(), new RulePackEvaluationInput
        {
            CaseType = caseType,
            Expedited = expedited,
            ReceivedAt = received,
            DecidedAt = decided,
        }, UsFederalHolidayCalendar.Instance);

    private static RuleDeadline Deadline(RulePackEvaluation e, string name) => e.Deadlines.Single(d => d.Name == name);

    // ── The pack ──────────────────────────────────────────────────

    [Fact]
    public void It_is_national_rather_than_a_States()
    {
        var pack = Pack();

        pack.IsNational.Should().BeTrue();
        pack.State.Should().BeNull();
        pack.Jurisdiction.Should().Be("US");
        pack.LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
    }

    [Fact]
    public void One_pack_serves_every_State()
    {
        foreach (var state in new[] { "TX", "WY", "ca" })
            Catalog.Find(state, LineOfBusiness.Medicare, AsOf)!.Id.Should().Be("medicare-advantage");
    }

    [Fact]
    public void A_States_own_pack_still_wins_over_the_national_one()
    {
        // Nothing uses this yet, but it is how a D-SNP State addendum would
        // land without the lookup being redesigned.
        var national = Pack();
        var texas = new RulePack
        {
            Id = "tx-medicare", State = "TX", LineOfBusiness = LineOfBusiness.Medicare,
            Version = "1", EffectiveFrom = new DateOnly(2024, 1, 1),
            Sources = national.Sources, MemberAppeal = national.MemberAppeal,
            MemberGrievance = national.MemberGrievance, Recoupment = national.Recoupment,
        };
        var catalog = new RulePackCatalog([national, texas]);

        catalog.Find("TX", LineOfBusiness.Medicare, AsOf)!.Id.Should().Be("tx-medicare");
        catalog.Find("WY", LineOfBusiness.Medicare, AsOf)!.Id.Should().Be("medicare-advantage");
    }

    [Fact]
    public void There_is_no_State_fair_hearing()
    {
        // An upheld Part C reconsideration goes to the IRE. A fair hearing
        // window here would be a Medicaid habit applied to the wrong program.
        Pack().MemberAppeal.FairHearingRequestWindow.Should().BeNull();

        Evaluate(RulePackCaseType.MemberAppeal, Utc(2026, 11, 2), decided: Utc(2026, 11, 20))
            .Deadlines.Should().NotContain(d => d.Name == "fairHearingRequest");
    }

    [Fact]
    public void Missing_the_deadline_sends_the_case_to_the_reviewer_not_the_member()
    {
        Pack().MemberAppeal.OnMissed.Should().Be(MissedDeadlineOutcome.AutoForwardToExternalReview);
    }

    [Fact]
    public void The_chain_runs_plan_to_IRE_to_ALJ_to_council_to_court()
    {
        Pack().MemberAppeal.Chain.Select(s => s.Owner).Should().Equal(
            LevelOwner.IndependentReviewer, LevelOwner.Regulator, LevelOwner.Regulator, LevelOwner.Court);
    }

    [Fact]
    public void A_contracted_providers_dispute_is_left_to_the_contract()
    {
        // CMS sets no process for one, which is a fact about the program and
        // not a gap in the pack.
        Pack().ProviderDispute.PlanDefined.Should().BeTrue();
    }

    [Fact]
    public void What_has_not_been_verified_is_said_so()
    {
        var pack = Pack();

        pack.ToConfirm.Should().NotBeEmpty();
        pack.ToConfirm.Should().Contain(t => t.Contains("payment-reconsideration"));
    }

    // ── The clocks it computes ────────────────────────────────────

    [Fact]
    public void A_standard_reconsideration_is_due_in_30_days()
    {
        var result = Evaluate(RulePackCaseType.MemberAppeal, Utc(2026, 11, 2));

        Deadline(result, "resolution").DueAt.Should().Be(Utc(2026, 12, 2));
        Deadline(result, "resolution").Citation.Should().Contain("422.590");
    }

    [Fact]
    public void An_expedited_reconsideration_is_due_in_72_hours()
    {
        Deadline(Evaluate(RulePackCaseType.MemberAppeal, Utc(2026, 11, 2), expedited: true), "resolution")
            .DueAt.Should().Be(Utc(2026, 11, 5));
    }

    [Fact]
    public void The_14_day_extension_applies_to_an_expedited_reconsideration_too()
    {
        // Unlike Medicaid's grievance rule, 422.590(f) extends both clocks.
        var extended = Deadline(Evaluate(RulePackCaseType.MemberAppeal, Utc(2026, 11, 2), expedited: true),
            "resolutionWithExtension");

        extended.DueAt.Should().Be(Utc(2026, 11, 19));
        extended.Note.Should().Contain("Part B drug");
    }

    [Fact]
    public void A_standard_grievance_is_due_in_30_days_not_the_Medicaid_90()
    {
        // The ceiling is the pack's own line of business. Before the floor
        // was split, every pack was measured against Medicaid's 90 days.
        var result = Evaluate(RulePackCaseType.MemberGrievance, Utc(2026, 11, 2));

        Deadline(result, "resolution").DueAt.Should().Be(Utc(2026, 12, 2));
        Deadline(result, "resolution").FederalCeilingAt.Should().Be(Utc(2026, 12, 2));
        Deadline(result, "resolution").Citation.Should().Contain("422.564");
    }

    [Fact]
    public void An_expedited_grievance_is_due_in_24_hours()
    {
        Deadline(Evaluate(RulePackCaseType.MemberGrievance, Utc(2026, 11, 2), expedited: true), "resolution")
            .DueAt.Should().Be(Utc(2026, 11, 3));
    }

    [Fact]
    public void The_federal_citations_quoted_are_Part_422_not_Part_438()
    {
        var appeal = Evaluate(RulePackCaseType.MemberAppeal, Utc(2026, 11, 2));
        var grievance = Evaluate(RulePackCaseType.MemberGrievance, Utc(2026, 11, 2));

        appeal.Deadlines.Concat(grievance.Deadlines)
              .Select(d => d.Citation).Where(c => c is not null)
              .Should().OnlyContain(c => c!.Contains("422."));
    }

    [Fact]
    public void The_packs_clocks_agree_with_the_policy_enforced_on_real_appeals()
    {
        // The pack and AppealResponseDeadlinePolicy must not drift: a member
        // services screen reading the pack and an appeal enforcing the
        // policy would otherwise show different due dates for one case.
        var received = Utc(2026, 11, 2);
        foreach (var (expedited, type, caseType) in new[]
                 {
                     (false, AppealType.Reconsideration, RulePackCaseType.MemberAppeal),
                     (true, AppealType.Reconsideration, RulePackCaseType.MemberAppeal),
                     (false, AppealType.Grievance, RulePackCaseType.MemberGrievance),
                 })
        {
            var fromPolicy = AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(
                received, LineOfBusiness.Medicare, type, AppealLevel.FirstLevel, expedited);

            Deadline(Evaluate(caseType, received, expedited), "resolution").DueAt.Should().Be(fromPolicy);
        }
    }
}

/// <summary>
/// The floor itself: what a pack is measured against, and what it refuses.
/// Packs were Medicaid-only, so these are the checks that let a second line
/// of business in without letting a wrong one through.
/// </summary>
public class RulePackFloorTests
{
    private static RulePack Minimal(LineOfBusiness lob, Clock? fairHearing, int filingDays = 60) => new()
    {
        Id = "test", LineOfBusiness = lob, Version = "1", EffectiveFrom = new DateOnly(2024, 1, 1),
        Sources = { ["s"] = new RuleSource { Citation = "a rule" } },
        MemberAppeal = new MemberAppealRules
        {
            FilingWindow = new Clock { Amount = filingDays, Unit = ClockUnit.CalendarDays, Source = "s" },
            Resolution = new Clock { Amount = 30, Unit = ClockUnit.CalendarDays, Source = "s" },
            ResolutionExpedited = new Clock { Amount = 72, Unit = ClockUnit.Hours, Source = "s" },
            Extension = new ExtensionRule { MaxDays = 14, Source = "s" },
            FairHearingRequestWindow = fairHearing,
        },
        MemberGrievance = new MemberGrievanceRules
        {
            Resolution = new Clock { Amount = 30, Unit = ClockUnit.CalendarDays, Source = "s" },
        },
        Recoupment = { new RecoupmentRule { Hold = RecoupmentHold.Unknown, Source = "s" } },
    };

    private static readonly Clock NinetyDays = new()
    {
        Amount = 90, Unit = ClockUnit.CalendarDays, From = ClockStart.Decision, Source = "s",
    };

    [Fact]
    public void Every_floors_ceilings_come_from_the_policy_rather_than_being_restated()
    {
        foreach (var floor in RulePackFloor.All)
        {
            floor.AppealCeiling.Should().Be(AppealResponseDeadlinePolicy.EnforceableMaximumWindow(
                floor.LineOfBusiness, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: false)!.Value);
            floor.GrievanceCeiling.Should().Be(AppealResponseDeadlinePolicy.EnforceableMaximumWindow(
                floor.LineOfBusiness, AppealType.Grievance, AppealLevel.FirstLevel, isUrgent: false)!.Value);
        }
    }

    [Fact]
    public void A_medicare_pack_with_a_fair_hearing_window_is_refused()
    {
        RulePackValidator.Validate(Minimal(LineOfBusiness.Medicare, NinetyDays))
            .Should().ContainSingle().Which.Should().Contain("does not apply to Medicare");
    }

    [Fact]
    public void A_medicaid_pack_without_one_is_refused()
    {
        RulePackValidator.Validate(Minimal(LineOfBusiness.Medicaid, fairHearing: null))
            .Should().Contain(e => e.Contains("fairHearingRequestWindow is required"));
    }

    [Fact]
    public void A_medicare_plan_may_give_the_member_longer_to_file_but_never_less()
    {
        RulePackValidator.Validate(Minimal(LineOfBusiness.Medicare, null, filingDays: 90)).Should().BeEmpty();
        RulePackValidator.Validate(Minimal(LineOfBusiness.Medicare, null, filingDays: 45))
            .Should().Contain(e => e.Contains("at least 60 calendar days"));
    }

    [Fact]
    public void A_medicaid_filing_window_is_exactly_60_days()
    {
        // 42 CFR 438.402(c)(2)(ii) sets the number outright rather than a floor.
        RulePackValidator.Validate(Minimal(LineOfBusiness.Medicaid, NinetyDays, filingDays: 90))
            .Should().Contain(e => e.Contains("must be 60 calendar days"));
    }

    [Fact]
    public void A_medicare_grievance_longer_than_30_days_is_refused()
    {
        // 90 days is lawful under Medicaid and not under Part C, which is
        // exactly what one shared floor could not say.
        static RulePack WithGrievance(LineOfBusiness lob, Clock? fairHearing, int days)
        {
            var pack = Minimal(lob, fairHearing);
            return new RulePack
            {
                Id = pack.Id, LineOfBusiness = lob, Version = pack.Version, EffectiveFrom = pack.EffectiveFrom,
                Sources = pack.Sources, MemberAppeal = pack.MemberAppeal, Recoupment = pack.Recoupment,
                MemberGrievance = new MemberGrievanceRules
                {
                    Resolution = new Clock { Amount = days, Unit = ClockUnit.CalendarDays, Source = "s" },
                },
            };
        }

        RulePackValidator.Validate(WithGrievance(LineOfBusiness.Medicaid, NinetyDays, 90)).Should().BeEmpty();
        RulePackValidator.Validate(WithGrievance(LineOfBusiness.Medicare, null, 90))
            .Should().Contain(e => e.Contains("memberGrievance.resolution") && e.Contains("422.564"));
    }

    [Fact]
    public void A_line_of_business_with_no_floor_is_refused_rather_than_guessed_at()
    {
        RulePackValidator.Validate(Minimal(LineOfBusiness.Marketplace, null))
            .Should().ContainSingle().Which.Should().Contain("no federal floor");
    }
}
