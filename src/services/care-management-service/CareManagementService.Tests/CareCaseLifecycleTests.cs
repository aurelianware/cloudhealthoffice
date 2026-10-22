using CareManagementService.Models;
using CareManagementService.Services;

namespace CareManagementService.Tests;

public class CareCaseLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    private static CareCase Case(CareCaseStatus status = CareCaseStatus.Referred, bool consented = false) => new()
    {
        TenantId = "t1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        Program = CareProgram.ComplexCaseManagement,
        Status = status,
        MemberConsentObtained = consented,
    };

    [Theory]
    [InlineData(CareCaseStatus.Referred, CareCaseStatus.Screening)]
    [InlineData(CareCaseStatus.Screening, CareCaseStatus.Outreach)]
    [InlineData(CareCaseStatus.Outreach, CareCaseStatus.Active)]
    [InlineData(CareCaseStatus.Active, CareCaseStatus.OnHold)]
    [InlineData(CareCaseStatus.OnHold, CareCaseStatus.Active)]
    public void The_ordinary_path_through_a_case_is_allowed(CareCaseStatus from, CareCaseStatus to)
    {
        CareCaseLifecycle.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(CareCaseStatus.Closed, CareCaseStatus.Active)]
    [InlineData(CareCaseStatus.Closed, CareCaseStatus.Screening)]
    [InlineData(CareCaseStatus.Declined, CareCaseStatus.Active)]
    public void A_closed_or_declined_case_is_terminal(CareCaseStatus from, CareCaseStatus to)
    {
        CareCaseLifecycle.CanTransition(from, to).Should().BeFalse(
            "a member who comes back gets a new case, so the episode's dates and outcome are not overwritten");
    }

    [Fact]
    public void A_case_cannot_skip_screening_and_outreach_straight_from_referral_to_active()
    {
        CareCaseLifecycle.CanTransition(CareCaseStatus.Referred, CareCaseStatus.Active).Should().BeFalse();
    }

    [Fact]
    public void A_case_cannot_be_made_active_without_the_members_consent()
    {
        var careCase = Case(CareCaseStatus.Outreach);

        var act = () => CareCaseLifecycle.Transition(careCase, CareCaseStatus.Active, Now, "cm-1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*voluntary*");
    }

    [Fact]
    public void Recording_consent_opens_the_way_to_an_active_case()
    {
        var careCase = Case(CareCaseStatus.Outreach);

        CareCaseLifecycle.RecordConsent(careCase, Now, "cm-1");
        CareCaseLifecycle.Transition(careCase, CareCaseStatus.Active, Now, "cm-1");

        careCase.Status.Should().Be(CareCaseStatus.Active);
        careCase.MemberConsentDate.Should().Be(Now);
        careCase.OpenedDate.Should().Be(Now);
    }

    [Fact]
    public void Consent_keeps_the_date_it_was_first_given()
    {
        var careCase = Case(CareCaseStatus.Outreach);
        CareCaseLifecycle.RecordConsent(careCase, Now, "cm-1");

        CareCaseLifecycle.RecordConsent(careCase, Now.AddDays(3), "cm-2");

        careCase.MemberConsentDate.Should().Be(Now);
    }

    [Fact]
    public void Closing_a_case_requires_an_outcome()
    {
        var act = () => CareCaseLifecycle.Transition(
            Case(CareCaseStatus.Active, consented: true), CareCaseStatus.Closed, Now, "cm-1", "Done");

        act.Should().Throw<ArgumentException>().WithMessage("*closure outcome*");
    }

    [Fact]
    public void Declining_requires_the_reason_the_member_gave()
    {
        var act = () => CareCaseLifecycle.Transition(
            Case(CareCaseStatus.Outreach), CareCaseStatus.Declined, Now, "cm-1");

        act.Should().Throw<ArgumentException>().WithMessage("*reason*");
    }

    [Fact]
    public void Closing_a_case_leaves_nothing_open_behind_it()
    {
        var careCase = Case(CareCaseStatus.Active, consented: true);
        careCase.CarePlan = new CarePlan
        {
            Status = CarePlanStatus.Active,
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily" }],
        };
        careCase.Activities =
        [
            new CareActivity { Id = "a1", Description = "Call member", Status = CareActivityStatus.Open },
            new CareActivity { Id = "a2", Description = "Mail packet", Status = CareActivityStatus.Completed },
        ];

        CareCaseLifecycle.Transition(
            careCase, CareCaseStatus.Closed, Now, "cm-1", "Goals met", CareCaseClosureOutcome.GoalsMet);

        careCase.CarePlan.Status.Should().Be(CarePlanStatus.Completed);
        careCase.Activities[0].Status.Should().Be(CareActivityStatus.Cancelled);
        careCase.Activities[1].Status.Should().Be(CareActivityStatus.Completed, "a finished activity is left alone");
        careCase.ClosureOutcome.Should().Be(CareCaseClosureOutcome.GoalsMet);
        careCase.ClosedDate.Should().Be(Now);
    }

    [Fact]
    public void Closing_a_case_whose_plan_was_never_activated_cancels_the_draft()
    {
        var careCase = Case(CareCaseStatus.Outreach);
        careCase.CarePlan = new CarePlan { Status = CarePlanStatus.Draft };

        CareCaseLifecycle.Transition(
            careCase, CareCaseStatus.Closed, Now, "cm-1", "Unreachable", CareCaseClosureOutcome.UnableToContact);

        careCase.CarePlan.Status.Should().Be(CarePlanStatus.Cancelled);
    }

    [Fact]
    public void Putting_a_case_on_hold_pauses_its_plan()
    {
        var careCase = Case(CareCaseStatus.Active, consented: true);
        careCase.CarePlan = new CarePlan { Status = CarePlanStatus.Active };

        CareCaseLifecycle.Transition(careCase, CareCaseStatus.OnHold, Now, "cm-1", "Member travelling");

        careCase.CarePlan.Status.Should().Be(CarePlanStatus.OnHold);
    }

    [Fact]
    public void Every_move_is_recorded_with_who_made_it_and_why()
    {
        var careCase = Case();

        CareCaseLifecycle.Transition(careCase, CareCaseStatus.Screening, Now, "cm-1", "Risk score 0.82");

        var entry = careCase.StatusHistory.Should().ContainSingle().Subject;
        entry.Status.Should().Be(CareCaseStatus.Screening);
        entry.ChangedBy.Should().Be("cm-1");
        entry.Reason.Should().Be("Risk score 0.82");
        entry.ChangedAt.Should().Be(Now);
    }

    [Fact]
    public void Moving_a_case_to_the_status_it_is_already_in_records_nothing()
    {
        var careCase = Case(CareCaseStatus.Screening);

        CareCaseLifecycle.Transition(careCase, CareCaseStatus.Screening, Now, "cm-1");

        careCase.StatusHistory.Should().BeEmpty();
    }

    [Fact]
    public void A_closed_case_cannot_record_consent()
    {
        var careCase = Case(CareCaseStatus.Closed);

        var act = () => CareCaseLifecycle.RecordConsent(careCase, Now, "cm-1");

        act.Should().Throw<InvalidOperationException>();
    }
}
