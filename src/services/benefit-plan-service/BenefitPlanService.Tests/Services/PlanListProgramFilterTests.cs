using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Reading a program's plans (docs/architecture/plan-configuration.md §12.6).
/// The plan → program link lives on the plan, so the only way to ask which
/// plans a program has is to filter the list on this side; tenant-service uses
/// it to confine a list of setting overrides to one branch of the hierarchy.
/// </summary>
public class PlanListProgramFilterTests
{
    private const string Tenant = "tenant-a";

    private static (BenefitPlanServiceImpl Service, InMemoryBenefitPlanRepository Repo) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var service = new BenefitPlanServiceImpl(repo, new InMemoryPlanVersionTransitionRepository(),
            new FakePlanVersionEventPublisher(), new NoOpNetworkTierSoftValidator(), new NoOpPlanLimitValidator(),
            NullLogger<BenefitPlanServiceImpl>.Instance);
        return (service, repo);
    }

    private static BenefitPlan Plan(string planId, string? programId, bool active = true) => new()
    {
        TenantId = Tenant,
        PlanId = planId,
        PlanName = planId,
        Payer = "Acme Health",
        ProgramId = programId,
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.HMO,
        LineOfBusiness = LineOfBusiness.Medicaid,
        IsActive = active,
    };

    private static async Task<(BenefitPlanServiceImpl Service, InMemoryBenefitPlanRepository Repo)> Seeded()
    {
        var (service, repo) = Build();
        await repo.CreateAsync(Plan("STAR-1", "t|DEFAULT|STAR"));
        await repo.CreateAsync(Plan("STAR-2", "t|DEFAULT|STAR", active: false));
        await repo.CreateAsync(Plan("PLUS-1", "t|DEFAULT|STARPLUS"));
        await repo.CreateAsync(Plan("OLD-1", null));
        return (service, repo);
    }

    [Fact]
    public async Task A_program_names_its_own_plans()
    {
        var (service, _) = await Seeded();

        var plans = await service.GetPlansAsync(Tenant, payer: null, planType: null, activeOnly: false, program: "t|DEFAULT|STAR");

        plans.Select(p => p.PlanId).Should().BeEquivalentTo("STAR-1", "STAR-2");
    }

    [Fact]
    public async Task A_plan_that_states_no_program_is_in_no_programs_list()
    {
        var (service, _) = await Seeded();

        // "Not stated" is not a match: a plan written before programs existed
        // belongs to no program until someone says which, and guessing one from
        // its line of business would place it in a state it may not operate in.
        var plans = await service.GetPlansAsync(Tenant, payer: null, planType: null, activeOnly: false, program: "t|DEFAULT|STARPLUS");

        plans.Select(p => p.PlanId).Should().Equal("PLUS-1");
    }

    [Fact]
    public async Task A_program_with_no_plans_is_an_empty_list_not_every_plan()
    {
        var (service, _) = await Seeded();

        (await service.GetPlansAsync(Tenant, null, null, activeOnly: false, program: "t|DEFAULT|NOPE"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Naming_no_program_still_lists_the_tenants_plans()
    {
        var (service, _) = await Seeded();

        (await service.GetPlansAsync(Tenant, null, null, activeOnly: false)).Should().HaveCount(4);
        // A blank program is not a filter either, so a caller that passes an
        // empty query string gets the list rather than nothing.
        (await service.GetPlansAsync(Tenant, null, null, activeOnly: false, program: "  ")).Should().HaveCount(4);
    }

    [Fact]
    public async Task The_program_filter_and_the_active_filter_both_apply()
    {
        var (service, _) = await Seeded();

        var plans = await service.GetPlansAsync(Tenant, null, null, activeOnly: true, program: "t|DEFAULT|STAR");

        plans.Select(p => p.PlanId).Should().Equal("STAR-1");
    }

    [Fact]
    public async Task One_tenants_program_does_not_reach_anothers_plans()
    {
        var (service, repo) = await Seeded();
        var other = Plan("STAR-3", "t|DEFAULT|STAR");
        other.TenantId = "tenant-b";
        await repo.CreateAsync(other);

        var plans = await service.GetPlansAsync(Tenant, null, null, activeOnly: false, program: "t|DEFAULT|STAR");

        plans.Select(p => p.PlanId).Should().BeEquivalentTo("STAR-1", "STAR-2");
    }
}
