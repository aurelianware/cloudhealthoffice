using CareManagementService.Controllers;
using CareManagementService.Models;
using CareManagementService.Repositories;
using CareManagementService.Services.Work;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace CareManagementService.Tests;

/// <summary>
/// The controller over a substituted repository: what it refuses, what it
/// stamps onto a case, and what it does when the work queue or a competing
/// write gets in the way.
/// </summary>
public class CareCasesControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
    private const string ActorId = "cm-user";

    private readonly Mock<ICareCaseRepository> _cases = new(MockBehavior.Strict);
    private readonly Mock<ICareWorkItemGateway> _work = new();

    private CareCasesController Controller()
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(ActorId);

        return new CareCasesController(
            _cases.Object, _work.Object, actor.Object, new FixedClock(Now));
    }

    private static CareCase Case(
        CareCaseStatus status = CareCaseStatus.Active,
        bool consented = true,
        CarePlan? plan = null) => new()
    {
        Id = "case-1",
        TenantId = "t1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        MemberFirstName = "Ada",
        MemberLastName = "Lovelace",
        Program = CareProgram.ComplexCaseManagement,
        Acuity = CareAcuity.High,
        Status = status,
        MemberConsentObtained = consented,
        MemberConsentDate = consented ? Now.AddDays(-1) : null,
        CarePlan = plan,
    };

    private void Stored(CareCase careCase)
    {
        _cases.Setup(c => c.GetByIdAsync(careCase.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(careCase);
    }

    private void SavesSuccessfully()
    {
        _cases.Setup(c => c.UpdateAsync(It.IsAny<CareCase>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((CareCase c, CancellationToken _) => c);
    }

    private void Missing(string id)
    {
        _cases.Setup(c => c.GetByIdAsync(id, It.IsAny<CancellationToken>()))
              .ReturnsAsync((CareCase?)null);
    }

    // ── Reading ───────────────────────────────────────────────────

    [Fact]
    public async Task A_case_that_does_not_exist_is_a_404_rather_than_an_empty_body()
    {
        Missing("nope");

        var result = await Controller().GetById("nope", default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_caseload_row_counts_open_activities_and_flags_an_overdue_contact()
    {
        var careCase = Case();
        careCase.NextContactDueDate = Now.AddDays(-3);
        careCase.Activities.Add(new CareActivity { Id = "a1", Status = CareActivityStatus.Open });
        careCase.Activities.Add(new CareActivity { Id = "a2", Status = CareActivityStatus.Completed });

        _cases.Setup(c => c.ListOpenAsync(null, null, 200, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { careCase });

        var result = await Controller().GetCaseload(null, null, 200, default);

        var rows = ((OkObjectResult)result.Result!).Value.As<IReadOnlyList<CareCaseSummary>>();
        rows.Should().ContainSingle();
        rows[0].MemberName.Should().Be("Lovelace, Ada");
        rows[0].OpenActivities.Should().Be(1);
        rows[0].ContactOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task A_closed_case_is_never_contact_overdue_however_old_its_contact_date()
    {
        var careCase = Case(CareCaseStatus.Closed);
        careCase.NextContactDueDate = Now.AddYears(-1);

        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { careCase });

        var result = await Controller().ListByMember("M1", default);

        var rows = ((OkObjectResult)result.Result!).Value.As<IReadOnlyList<CareCaseSummary>>();
        rows[0].ContactOverdue.Should().BeFalse("a closed case is nobody's outreach to chase");
    }

    [Fact]
    public async Task The_overdue_list_is_asked_for_as_of_the_controllers_clock()
    {
        _cases.Setup(c => c.ListContactOverdueAsync(Now, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<CareCase>());

        await Controller().GetContactOverdue(default);

        _cases.Verify(c => c.ListContactOverdueAsync(Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Opening a case ────────────────────────────────────────────

    [Fact]
    public async Task An_opened_case_starts_as_referred_with_the_actor_and_a_first_history_entry()
    {
        CareCase? saved = null;
        _cases.Setup(c => c.CreateAsync(It.IsAny<CareCase>(), It.IsAny<CancellationToken>()))
              .Callback((CareCase c, CancellationToken _) => saved = c)
              .ReturnsAsync((CareCase c, CancellationToken _) => c);

        var result = await Controller().Open(new OpenCareCaseRequest
        {
            MemberId = "M1",
            Program = CareProgram.TransitionsOfCare,
            ReferralReason = "Discharged after a 6 day stay",
        }, default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        saved!.Status.Should().Be(CareCaseStatus.Referred);
        saved.ReferredDate.Should().Be(Now);
        saved.CreatedBy.Should().Be(ActorId);
        saved.CaseNumber.Should().StartWith("CM-20261022-", "an unnamed case still gets a readable number");
        saved.StatusHistory.Should().ContainSingle()
             .Which.Reason.Should().Be("Discharged after a 6 day stay");
    }

    [Fact]
    public async Task A_case_number_already_in_the_tenant_is_a_conflict()
    {
        _cases.Setup(c => c.CreateAsync(It.IsAny<CareCase>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new DuplicateCaseNumberException("CM-1"));

        var result = await Controller().Open(
            new OpenCareCaseRequest { MemberId = "M1", CaseNumber = "CM-1" }, default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    // ── Status and consent ────────────────────────────────────────

    [Fact]
    public async Task A_transition_the_lifecycle_forbids_is_a_conflict_and_is_never_saved()
    {
        Stored(Case(CareCaseStatus.Closed));

        var result = await Controller().SetStatus(
            "case-1", new CareCaseStatusRequest { Status = CareCaseStatus.Active }, default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        _cases.Verify(c => c.UpdateAsync(It.IsAny<CareCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Closing_without_an_outcome_is_refused_as_a_bad_request()
    {
        Stored(Case());

        var result = await Controller().SetStatus(
            "case-1", new CareCaseStatusRequest { Status = CareCaseStatus.Closed }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Recording_consent_stamps_the_date_and_the_actor()
    {
        var careCase = Case(CareCaseStatus.Outreach, consented: false);
        Stored(careCase);
        SavesSuccessfully();

        var result = await Controller().RecordConsent("case-1", default);

        result.Result.Should().BeOfType<OkObjectResult>();
        careCase.MemberConsentObtained.Should().BeTrue();
        careCase.MemberConsentDate.Should().Be(Now);
    }

    [Fact]
    public async Task A_write_that_loses_a_race_is_a_conflict_rather_than_a_silent_overwrite()
    {
        Stored(Case(CareCaseStatus.Outreach, consented: false));
        _cases.Setup(c => c.UpdateAsync(It.IsAny<CareCase>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new CareCaseConcurrencyException("case-1", 3));

        var result = await Controller().RecordConsent("case-1", default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    // ── The care plan ─────────────────────────────────────────────

    [Fact]
    public async Task A_plan_whose_goal_addresses_a_problem_it_does_not_carry_is_refused()
    {
        Stored(Case());

        var plan = new CarePlan
        {
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily", ProblemIds = ["missing"] }],
        };

        var result = await Controller().SetCarePlan("case-1", plan, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task A_put_cannot_activate_a_plan_by_setting_its_status()
    {
        var careCase = Case();
        Stored(careCase);
        SavesSuccessfully();

        var plan = new CarePlan
        {
            Status = CarePlanStatus.Active,
            ActivatedDate = Now,
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily" }],
        };

        await Controller().SetCarePlan("case-1", plan, default);

        careCase.CarePlan!.Status.Should().Be(CarePlanStatus.Draft,
            "activation goes through its own endpoint, which checks consent");
        careCase.CarePlan.ActivatedDate.Should().BeNull();
        careCase.CarePlan.LastUpdatedBy.Should().Be(ActorId);
    }

    [Fact]
    public async Task A_plan_cannot_be_activated_without_the_members_consent()
    {
        Stored(Case(consented: false, plan: new CarePlan
        {
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily" }],
        }));

        var result = await Controller().ActivateCarePlan("case-1", default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Activating_a_plan_promotes_its_proposed_goals()
    {
        var careCase = Case(plan: new CarePlan
        {
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily", Status = CareGoalStatus.Proposed }],
        });
        Stored(careCase);
        SavesSuccessfully();

        var result = await Controller().ActivateCarePlan("case-1", default);

        result.Result.Should().BeOfType<OkObjectResult>();
        careCase.CarePlan!.Status.Should().Be(CarePlanStatus.Active);
        careCase.CarePlan.Goals[0].Status.Should().Be(CareGoalStatus.InProgress);
    }

    [Fact]
    public async Task Progress_against_a_goal_the_plan_does_not_carry_is_a_404()
    {
        Stored(Case(plan: new CarePlan
        {
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily" }],
        }));

        var result = await Controller().RecordGoalProgress(
            "case-1", "g-other",
            new GoalProgressRequest { Status = CareGoalStatus.Achieved, Note = "Done" },
            default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Progress_on_a_case_with_no_plan_at_all_is_a_404()
    {
        Stored(Case());

        var result = await Controller().RecordGoalProgress(
            "case-1", "g1", new GoalProgressRequest { Status = CareGoalStatus.Achieved, Note = "Done" }, default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Progress_without_a_note_is_refused()
    {
        Stored(Case(plan: new CarePlan
        {
            Goals = [new CareGoal { Id = "g1", Description = "Walk daily" }],
        }));

        var result = await Controller().RecordGoalProgress(
            "case-1", "g1", new GoalProgressRequest { Status = CareGoalStatus.Achieved, Note = "  " }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Assessments and activities ────────────────────────────────

    [Fact]
    public async Task A_completed_assessment_is_stamped_with_who_completed_it_and_when()
    {
        var careCase = Case();
        Stored(careCase);
        SavesSuccessfully();

        await Controller().AddAssessment("case-1", new CareAssessment
        {
            TemplateId = "phq-9",
            TemplateVersion = "2.1",
            Status = CareAssessmentStatus.Completed,
        }, default);

        var assessment = careCase.Assessments.Should().ContainSingle().Subject;
        assessment.CompletedAt.Should().Be(Now);
        assessment.CompletedBy.Should().Be(ActorId);
    }

    [Fact]
    public async Task An_assessment_still_in_progress_is_not_stamped_as_completed()
    {
        var careCase = Case();
        Stored(careCase);
        SavesSuccessfully();

        await Controller().AddAssessment("case-1", new CareAssessment
        {
            TemplateId = "phq-9",
            Status = CareAssessmentStatus.InProgress,
        }, default);

        careCase.Assessments[0].CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_new_activity_falls_to_the_care_manager_and_carries_its_work_item_id()
    {
        var careCase = Case();
        careCase.CareManagerId = "cm-7";
        Stored(careCase);
        SavesSuccessfully();

        _work.Setup(w => w.EnqueueAsync(It.IsAny<CareWorkItemCommand>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync("wi-99");

        await Controller().AddActivity("case-1", new CareActivity { Id = "a1", Description = "Call member" }, default);

        var activity = careCase.Activities.Should().ContainSingle().Subject;
        activity.AssignedTo.Should().Be("cm-7");
        activity.Status.Should().Be(CareActivityStatus.Open);
        activity.CreatedBy.Should().Be(ActorId);
        activity.WorkItemId.Should().Be("wi-99");
    }

    [Fact]
    public async Task An_activity_is_recorded_even_when_the_work_queue_refuses_it()
    {
        var careCase = Case();
        Stored(careCase);
        SavesSuccessfully();

        _work.Setup(w => w.EnqueueAsync(It.IsAny<CareWorkItemCommand>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((string?)null);

        var result = await Controller().AddActivity(
            "case-1", new CareActivity { Id = "a1", Description = "Call member" }, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        careCase.Activities.Should().ContainSingle()
                .Which.WorkItemId.Should().BeNull("the care record does not depend on the queue being up");
    }

    [Fact]
    public async Task Completing_an_activity_closes_the_work_item_behind_it()
    {
        var careCase = Case();
        careCase.Activities.Add(new CareActivity { Id = "a1", WorkItemId = "wi-99" });
        Stored(careCase);
        SavesSuccessfully();

        await Controller().CompleteActivity(
            "case-1", "A1", new CompleteActivityRequest { Outcome = "Reached" }, default);

        careCase.Activities[0].Status.Should().Be(CareActivityStatus.Completed);
        careCase.Activities[0].CompletedAt.Should().Be(Now);
        _work.Verify(w => w.CompleteAsync("t1", "wi-99", "Reached", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Completing_an_activity_that_never_reached_the_queue_touches_no_work_item()
    {
        var careCase = Case();
        careCase.Activities.Add(new CareActivity { Id = "a1", WorkItemId = null });
        Stored(careCase);
        SavesSuccessfully();

        await Controller().CompleteActivity("case-1", "a1", new CompleteActivityRequest(), default);

        careCase.Activities[0].Status.Should().Be(CareActivityStatus.Completed);
        _work.Verify(
            w => w.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Completing_an_activity_the_case_does_not_carry_is_a_404()
    {
        Stored(Case());

        var result = await Controller().CompleteActivity(
            "case-1", "nope", new CompleteActivityRequest(), default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
