using CareManagementService.Models;
using CareManagementService.Services;

namespace CareManagementService.Tests;

public class CarePlanPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    private static CarePlan Plan() => new()
    {
        Problems = [new CareProblem { Id = "p1", Description = "Uncontrolled type 2 diabetes", DiagnosisCode = "E11.9" }],
        Goals =
        [
            new CareGoal
            {
                Id = "g1",
                Description = "HbA1c below 8",
                ProblemIds = ["p1"],
                Measure = "HbA1c",
                TargetDate = Now.AddDays(90),
            },
        ],
        Interventions =
        [
            new CareIntervention
            {
                Id = "i1",
                Description = "Weekly coaching call",
                GoalIds = ["g1"],
                ResponsibleParty = "care manager",
                Frequency = "weekly",
            },
        ],
    };

    private static CareCase CaseWith(CarePlan? plan, bool consented = true) => new()
    {
        TenantId = "t1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        Program = CareProgram.DiseaseManagement,
        Status = CareCaseStatus.Active,
        MemberConsentObtained = consented,
        CarePlan = plan,
    };

    // ── Validation ────────────────────────────────────────────────

    [Fact]
    public void A_coherent_plan_validates()
    {
        var act = () => CarePlanPolicy.Validate(Plan());

        act.Should().NotThrow();
    }

    [Fact]
    public void A_goal_pointing_at_a_problem_that_is_not_on_the_plan_is_refused()
    {
        var plan = Plan();
        plan.Goals[0].ProblemIds = ["p-gone"];

        var act = () => CarePlanPolicy.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*p-gone*");
    }

    [Fact]
    public void An_intervention_pointing_at_a_goal_that_is_not_on_the_plan_is_refused()
    {
        var plan = Plan();
        plan.Interventions[0].GoalIds = ["g-gone"];

        var act = () => CarePlanPolicy.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*g-gone*");
    }

    [Fact]
    public void Duplicate_goal_ids_are_refused()
    {
        var plan = Plan();
        plan.Goals.Add(new CareGoal { Id = "g1", Description = "Something else" });

        var act = () => CarePlanPolicy.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Fact]
    public void An_intervention_that_ends_before_it_starts_is_refused()
    {
        var plan = Plan();
        plan.Interventions[0].StartDate = Now;
        plan.Interventions[0].EndDate = Now.AddDays(-1);

        var act = () => CarePlanPolicy.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*ends before it starts*");
    }

    // ── Activation ────────────────────────────────────────────────

    [Fact]
    public void Activating_a_plan_puts_its_proposed_goals_into_progress()
    {
        var careCase = CaseWith(Plan());

        CarePlanPolicy.Activate(careCase, Now, "cm-1");

        careCase.CarePlan!.Status.Should().Be(CarePlanStatus.Active);
        careCase.CarePlan.ActivatedDate.Should().Be(Now);
        careCase.CarePlan.Goals[0].Status.Should().Be(CareGoalStatus.InProgress);
    }

    [Fact]
    public void A_plan_cannot_be_activated_without_the_members_consent()
    {
        var careCase = CaseWith(Plan(), consented: false);

        var act = () => CarePlanPolicy.Activate(careCase, Now, "cm-1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*voluntary*");
    }

    [Fact]
    public void A_plan_with_no_goal_cannot_be_activated()
    {
        var plan = Plan();
        plan.Goals.Clear();
        plan.Interventions.Clear();

        var act = () => CarePlanPolicy.Activate(CaseWith(plan), Now, "cm-1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one goal*");
    }

    [Fact]
    public void A_closed_case_cannot_have_its_plan_activated()
    {
        var careCase = CaseWith(Plan());
        careCase.Status = CareCaseStatus.Closed;

        var act = () => CarePlanPolicy.Activate(careCase, Now, "cm-1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_case_with_no_plan_has_nothing_to_activate()
    {
        var act = () => CarePlanPolicy.Activate(CaseWith(null), Now, "cm-1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no care plan*");
    }

    [Fact]
    public void Activating_an_already_active_plan_keeps_the_original_activation_date()
    {
        var careCase = CaseWith(Plan());
        CarePlanPolicy.Activate(careCase, Now, "cm-1");

        CarePlanPolicy.Activate(careCase, Now.AddDays(5), "cm-2");

        careCase.CarePlan!.ActivatedDate.Should().Be(Now);
    }

    // ── Goal progress ─────────────────────────────────────────────

    [Fact]
    public void Recording_progress_moves_the_goal_and_keeps_the_note()
    {
        var plan = Plan();

        CarePlanPolicy.RecordGoalProgress(plan, "g1", CareGoalStatus.Achieved, "HbA1c 7.4 at last draw", Now, "cm-1");

        var goal = plan.Goals[0];
        goal.Status.Should().Be(CareGoalStatus.Achieved);
        goal.AchievedDate.Should().Be(Now);
        goal.Progress.Should().ContainSingle()
            .Which.Note.Should().Be("HbA1c 7.4 at last draw");
    }

    [Fact]
    public void A_goal_that_slips_back_out_of_achieved_loses_its_achieved_date()
    {
        var plan = Plan();
        CarePlanPolicy.RecordGoalProgress(plan, "g1", CareGoalStatus.Achieved, "Met", Now, "cm-1");

        CarePlanPolicy.RecordGoalProgress(plan, "g1", CareGoalStatus.InProgress, "Slipped back", Now.AddDays(30), "cm-1");

        plan.Goals[0].AchievedDate.Should().BeNull();
        plan.Goals[0].Progress.Should().HaveCount(2);
    }

    [Fact]
    public void Progress_without_a_note_is_refused()
    {
        var act = () => CarePlanPolicy.RecordGoalProgress(Plan(), "g1", CareGoalStatus.Achieved, "  ", Now);

        act.Should().Throw<ArgumentException>().WithMessage("*note*");
    }

    [Fact]
    public void Progress_against_a_goal_that_is_not_on_the_plan_is_refused()
    {
        var act = () => CarePlanPolicy.RecordGoalProgress(Plan(), "g-gone", CareGoalStatus.Achieved, "Met", Now);

        act.Should().Throw<KeyNotFoundException>();
    }

    // ── Progress summary ──────────────────────────────────────────

    [Fact]
    public void The_summary_counts_only_goals_anyone_pursued()
    {
        var plan = Plan();
        plan.Goals =
        [
            new CareGoal { Id = "g1", Description = "a", Status = CareGoalStatus.Achieved },
            new CareGoal { Id = "g2", Description = "b", Status = CareGoalStatus.NotAchieved },
            new CareGoal { Id = "g3", Description = "c", Status = CareGoalStatus.Cancelled },
            new CareGoal { Id = "g4", Description = "d", Status = CareGoalStatus.Proposed },
        ];
        plan.Interventions.Clear();

        var progress = CarePlanPolicy.Summarize(plan, Now);

        progress.TotalGoals.Should().Be(4);
        progress.AchievedGoals.Should().Be(1);
        progress.CompletionRate.Should().Be(0.5m,
            "a cancelled or merely proposed goal is neither a success nor a failure");
    }

    [Fact]
    public void An_in_progress_goal_past_its_target_date_is_overdue()
    {
        var plan = Plan();
        plan.Goals[0].Status = CareGoalStatus.InProgress;
        plan.Goals[0].TargetDate = Now.AddDays(-1);

        CarePlanPolicy.Summarize(plan, Now).OverdueGoals.Should().Be(1);
    }

    [Fact]
    public void An_achieved_goal_past_its_target_date_is_not_overdue()
    {
        var plan = Plan();
        plan.Goals[0].Status = CareGoalStatus.Achieved;
        plan.Goals[0].TargetDate = Now.AddDays(-1);

        CarePlanPolicy.Summarize(plan, Now).OverdueGoals.Should().Be(0);
    }

    [Fact]
    public void Open_barriers_are_counted_and_resolved_ones_are_not()
    {
        var plan = Plan();
        plan.Barriers =
        [
            new CareBarrier { Id = "b1", Description = "No transport", Category = "transportation" },
            new CareBarrier { Id = "b2", Description = "Cost", Resolved = true },
        ];

        CarePlanPolicy.Summarize(plan, Now).OpenBarriers.Should().Be(1);
    }

    [Fact]
    public void A_case_with_no_plan_summarizes_to_zeroes_rather_than_throwing()
    {
        var progress = CarePlanPolicy.Summarize(null, Now);

        progress.TotalGoals.Should().Be(0);
        progress.CompletionRate.Should().Be(0m);
    }
}
