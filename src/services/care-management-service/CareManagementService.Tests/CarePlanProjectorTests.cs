using CareManagementService.Models;
using CareManagementService.Services.Fhir;

namespace CareManagementService.Tests;

public class CarePlanProjectorTests
{
    private static readonly DateTime Activated = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CareCase Case() => new()
    {
        Id = "case-1",
        TenantId = "t1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        Program = CareProgram.TransitionsOfCare,
        Status = CareCaseStatus.Active,
        CareTeam =
        [
            new CareTeamMember { Name = "Dr Osei", Role = "PCP", Npi = "1234567893" },
            new CareTeamMember { Name = "Jo Reyes", Role = "daughter", External = true },
        ],
        CarePlan = new CarePlan
        {
            Status = CarePlanStatus.Active,
            ActivatedDate = Activated,
            Problems = [new CareProblem { Id = "p1", Description = "Heart failure", DiagnosisCode = "I50.9" }],
            Goals =
            [
                new CareGoal
                {
                    Id = "g1",
                    Description = "No readmission within 30 days",
                    ProblemIds = ["p1"],
                    Status = CareGoalStatus.InProgress,
                    TargetDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                    Measure = "readmissions",
                    Progress =
                    [
                        new CareGoalProgress
                        {
                            RecordedAt = Activated.AddDays(7),
                            Status = CareGoalStatus.InProgress,
                            Note = "Weight stable, taking diuretic as prescribed.",
                        },
                    ],
                },
            ],
            Interventions =
            [
                new CareIntervention
                {
                    Id = "i1",
                    Description = "Post-discharge call",
                    GoalIds = ["g1"],
                    ResponsibleParty = "care manager",
                    Frequency = "weekly",
                },
            ],
            Barriers = [new CareBarrier { Id = "b1", Description = "No transport to clinic", Category = "transportation" }],
        },
    };

    [Fact]
    public void A_care_plan_projects_as_a_plan_of_record_for_the_member()
    {
        var resource = CarePlanProjector.ToCarePlan(Case())!;

        resource["resourceType"]!.GetValue<string>().Should().Be("CarePlan");
        resource["id"]!.GetValue<string>().Should().Be("case-1");
        resource["status"]!.GetValue<string>().Should().Be("active");
        resource["intent"]!.GetValue<string>().Should().Be("plan");
        resource["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/M1");
        resource["period"]!["start"]!.GetValue<string>().Should().Be("2026-09-01");
    }

    [Fact]
    public void Interventions_become_activities_carrying_who_and_how_often()
    {
        var resource = CarePlanProjector.ToCarePlan(Case())!;

        var description = resource["activity"]![0]!["detail"]!["description"]!.GetValue<string>();
        description.Should().Contain("Post-discharge call");
        description.Should().Contain("responsible: care manager");
        description.Should().Contain("frequency: weekly");
    }

    [Fact]
    public void An_open_barrier_is_carried_as_a_note()
    {
        var resource = CarePlanProjector.ToCarePlan(Case())!;

        resource["note"]![0]!["text"]!.GetValue<string>()
            .Should().Be("Barrier (transportation): No transport to clinic");
    }

    [Fact]
    public void A_resolved_barrier_is_not_carried()
    {
        var careCase = Case();
        careCase.CarePlan!.Barriers[0].Resolved = true;

        CarePlanProjector.ToCarePlan(careCase)!["note"].Should().BeNull();
    }

    [Fact]
    public void A_problem_with_a_diagnosis_code_is_coded_and_one_without_is_text_only()
    {
        var careCase = Case();
        careCase.CarePlan!.Problems.Add(new CareProblem { Id = "p2", Description = "Food insecurity" });

        var addresses = CarePlanProjector.ToCarePlan(careCase)!["addresses"]!.AsArray();

        addresses[0]!["concept"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("I50.9");
        addresses[1]!["concept"]!["coding"].Should().BeNull();
        addresses[1]!["concept"]!["text"]!.GetValue<string>().Should().Be("Food insecurity");
    }

    [Fact]
    public void A_case_with_no_plan_projects_no_care_plan()
    {
        var careCase = Case();
        careCase.CarePlan = null;

        CarePlanProjector.ToCarePlan(careCase).Should().BeNull();
        CarePlanProjector.ToGoals(careCase).Should().BeEmpty();
    }

    [Theory]
    [InlineData(CarePlanStatus.Draft, "draft")]
    [InlineData(CarePlanStatus.Active, "active")]
    [InlineData(CarePlanStatus.OnHold, "on-hold")]
    [InlineData(CarePlanStatus.Completed, "completed")]
    [InlineData(CarePlanStatus.Cancelled, "revoked")]
    public void Plan_statuses_map_to_the_FHIR_request_statuses(CarePlanStatus status, string expected)
    {
        var careCase = Case();
        careCase.CarePlan!.Status = status;

        CarePlanProjector.ToCarePlan(careCase)!["status"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void A_goal_carries_its_achievement_status_its_target_and_its_progress_notes()
    {
        var goal = CarePlanProjector.ToGoals(Case()).Should().ContainSingle().Subject;

        goal["resourceType"]!.GetValue<string>().Should().Be("Goal");
        goal["id"]!.GetValue<string>().Should().Be("case-1-g1");
        goal["lifecycleStatus"]!.GetValue<string>().Should().Be("active");
        goal["achievementStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("in-progress");
        goal["target"]![0]!["dueDate"]!.GetValue<string>().Should().Be("2026-10-01");
        goal["note"]![0]!["text"]!.GetValue<string>().Should().Contain("Weight stable");
    }

    [Theory]
    [InlineData(CareGoalStatus.Achieved, "completed", "achieved")]
    [InlineData(CareGoalStatus.NotAchieved, "completed", "not-achieved")]
    [InlineData(CareGoalStatus.Cancelled, "cancelled", "in-progress")]
    [InlineData(CareGoalStatus.Proposed, "proposed", "in-progress")]
    public void Goal_statuses_map_to_lifecycle_and_achievement(
        CareGoalStatus status, string lifecycle, string achievement)
    {
        var careCase = Case();
        careCase.CarePlan!.Goals[0].Status = status;

        var goal = CarePlanProjector.ToGoals(careCase).Single();
        goal["lifecycleStatus"]!.GetValue<string>().Should().Be(lifecycle);
        goal["achievementStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(achievement);
    }

    [Fact]
    public void A_practitioner_is_referenced_by_NPI_and_a_family_caregiver_is_only_named()
    {
        var team = CarePlanProjector.ToCareTeam(Case())!;

        team["participant"]![0]!["member"]!["reference"]!.GetValue<string>().Should().Be("Practitioner/1234567893");
        team["participant"]![1]!["member"]!["reference"].Should().BeNull(
            "a family caregiver has no NPI and no resource in CHO to point at");
        team["participant"]![1]!["member"]!["display"]!.GetValue<string>().Should().Be("Jo Reyes");
    }

    [Fact]
    public void A_closed_cases_care_team_is_inactive()
    {
        var careCase = Case();
        careCase.Status = CareCaseStatus.Closed;

        CarePlanProjector.ToCareTeam(careCase)!["status"]!.GetValue<string>().Should().Be("inactive");
    }

    [Fact]
    public void A_case_with_nobody_on_the_team_projects_no_care_team()
    {
        var careCase = Case();
        careCase.CareTeam.Clear();

        CarePlanProjector.ToCareTeam(careCase).Should().BeNull();
    }
}
