using System.Text.Json.Nodes;
using CareManagementService.Controllers;
using CareManagementService.Models;
using CareManagementService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace CareManagementService.Tests;

/// <summary>
/// The read-only FHIR projection endpoints: what they serve, what they
/// refuse, and that they answer in FHIR rather than in plain problem JSON.
/// </summary>
public class FhirCarePlanControllerTests
{
    private readonly Mock<ICareCaseRepository> _cases = new();

    private FhirCarePlanController Controller() => new(_cases.Object);

    private static CareCase Case(string id = "case-1", CarePlan? plan = null) => new()
    {
        Id = id,
        TenantId = "t1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        MemberFirstName = "Ada",
        MemberLastName = "Lovelace",
        Program = CareProgram.ComplexCaseManagement,
        Status = CareCaseStatus.Active,
        CareManagerId = "cm-7",
        CareManagerName = "Grace Hopper",
        CarePlan = plan,
        CareTeam =
        [
            new CareTeamMember { Name = "Grace Hopper", Role = "Care manager", Npi = "1234567893" },
        ],
    };

    /// <summary>A case with nobody on its care team, which projects no CareTeam at all.</summary>
    private static CareCase CaseWithNoTeam()
    {
        var careCase = Case("case-3");
        careCase.CareTeam.Clear();
        return careCase;
    }

    private static CarePlan Plan() => new()
    {
        Status = CarePlanStatus.Active,
        Goals = [new CareGoal { Id = "g1", Description = "Walk daily", Status = CareGoalStatus.InProgress }],
    };

    private static JsonObject Body(IActionResult result) =>
        ((ObjectResult)result).Value.As<JsonObject>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Searching_care_plans_without_a_patient_is_an_OperationOutcome(string? patient)
    {
        var result = await Controller().SearchCarePlans(patient, default);

        var body = Body(result);
        ((BadRequestObjectResult)result).StatusCode.Should().Be(400);
        body["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
        body["issue"]![0]!["code"]!.GetValue<string>().Should().Be("required");
    }

    [Fact]
    public async Task A_patient_reference_may_be_written_either_way()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<CareCase>());

        await Controller().SearchCarePlans("Patient/M1", default);
        await Controller().SearchCarePlans("M1", default);

        _cases.Verify(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_care_plan_search_comes_back_as_a_searchset_bundle()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Case(plan: Plan()), Case("case-2", Plan()) });

        var body = Body(await Controller().SearchCarePlans("Patient/M1", default));

        body["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        body["type"]!.GetValue<string>().Should().Be("searchset");
        body["total"]!.GetValue<int>().Should().Be(2);
        body["entry"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_case_with_no_care_plan_contributes_nothing_to_the_bundle()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Case(plan: Plan()), Case("case-2") });

        var body = Body(await Controller().SearchCarePlans("M1", default));

        body["total"]!.GetValue<int>().Should().Be(1,
            "a case without a plan has no CarePlan resource to project");
    }

    [Fact]
    public async Task One_care_plan_is_served_by_its_case_id()
    {
        _cases.Setup(c => c.GetByIdAsync("case-1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Case(plan: Plan()));

        var body = Body(await Controller().GetCarePlan("case-1", default));

        body["resourceType"]!.GetValue<string>().Should().Be("CarePlan");
    }

    [Fact]
    public async Task A_case_that_does_not_exist_answers_a_not_found_OperationOutcome()
    {
        _cases.Setup(c => c.GetByIdAsync("nope", It.IsAny<CancellationToken>()))
              .ReturnsAsync((CareCase?)null);

        var result = await Controller().GetCarePlan("nope", default);

        ((ObjectResult)result).StatusCode.Should().Be(404);
        Body(result)["issue"]![0]!["code"]!.GetValue<string>().Should().Be("not-found");
    }

    [Fact]
    public async Task A_case_that_exists_but_carries_no_plan_is_also_a_not_found_outcome()
    {
        _cases.Setup(c => c.GetByIdAsync("case-1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Case());

        var result = await Controller().GetCarePlan("case-1", default);

        ((ObjectResult)result).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Goals_from_every_one_of_a_members_plans_come_back_together()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Case(plan: Plan()), Case("case-2", Plan()) });

        var body = Body(await Controller().SearchGoals("Patient/M1", default));

        body["total"]!.GetValue<int>().Should().Be(2);
        body["entry"]![0]!["resource"]!["resourceType"]!.GetValue<string>().Should().Be("Goal");
    }

    [Fact]
    public async Task Searching_goals_without_a_patient_is_refused()
    {
        var result = await Controller().SearchGoals(null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task A_care_team_is_served_by_its_case_id()
    {
        _cases.Setup(c => c.GetByIdAsync("case-1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Case());

        var body = Body(await Controller().GetCareTeam("case-1", default));

        body["resourceType"]!.GetValue<string>().Should().Be("CareTeam");
    }

    [Fact]
    public async Task A_care_team_for_a_case_that_does_not_exist_is_a_not_found_outcome()
    {
        _cases.Setup(c => c.GetByIdAsync("nope", It.IsAny<CancellationToken>()))
              .ReturnsAsync((CareCase?)null);

        var result = await Controller().GetCareTeam("nope", default);

        ((ObjectResult)result).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Care_teams_can_be_searched_by_member()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Case() });

        var body = Body(await Controller().SearchCareTeams("Patient/M1", default));

        body["type"]!.GetValue<string>().Should().Be("searchset");
        body["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task A_case_with_nobody_on_its_care_team_projects_no_CareTeam()
    {
        _cases.Setup(c => c.GetByIdAsync("case-3", It.IsAny<CancellationToken>()))
              .ReturnsAsync(CaseWithNoTeam());

        var result = await Controller().GetCareTeam("case-3", default);

        ((ObjectResult)result).StatusCode.Should().Be(404,
            "an empty team is not a care team, and FHIR says so with an OperationOutcome");
    }

    [Fact]
    public async Task A_member_whose_cases_have_no_care_team_gets_an_empty_bundle()
    {
        _cases.Setup(c => c.ListByMemberAsync("M1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { CaseWithNoTeam() });

        var body = Body(await Controller().SearchCareTeams("M1", default));

        body["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Searching_care_teams_without_a_patient_is_refused()
    {
        var result = await Controller().SearchCareTeams("", default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Every_projection_is_served_as_fhir_json()
    {
        _cases.Setup(c => c.GetByIdAsync("case-1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Case(plan: Plan()));

        var result = (ObjectResult)await Controller().GetCarePlan("case-1", default);

        result.ContentTypes.Should().Contain("application/fhir+json");
    }
}
