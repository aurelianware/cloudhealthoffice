using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Repositories;

/// <summary>
/// End-to-end round-trip of typed benefits through the repository contract
/// and the version-chain lifecycle, exercised via
/// <see cref="InMemoryBenefitPlanRepository"/>. The fake clones via the
/// same System.Text.Json Web options that real Cosmos / Mongo wire formats
/// will go through, so the discriminator and every type-specific facet
/// must survive store-and-fetch and the <c>amend → publish v2</c> path.
/// </summary>
public class TypedBenefitRoundTripTests
{
    private const string Tenant = "tenant-typed";
    private const string Actor = "user-typed";

    private static (BenefitPlanServiceImpl service,
                    InMemoryBenefitPlanRepository repo,
                    InMemoryPlanVersionTransitionRepository transitions,
                    FakePlanVersionEventPublisher events) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var transitions = new InMemoryPlanVersionTransitionRepository();
        var events = new FakePlanVersionEventPublisher();
        var service = new BenefitPlanServiceImpl(repo, transitions, events, new NoOpNetworkTierSoftValidator(), new NoOpPlanLimitValidator(), NullLogger<BenefitPlanServiceImpl>.Instance);
        return (service, repo, transitions, events);
    }

    private static BenefitPlan PlanWithTypedBenefits(string planId = "plan-typed") => new()
    {
        TenantId = Tenant,
        PlanId = planId,
        PlanName = "Mixed Typed",
        Payer = "Acme",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        Benefits =
        {
            new MedicalBenefit { ServiceCategory = "Primary Care", CopayAmount = 25m },
            new PharmacyBenefit
            {
                ServiceCategory = "Pharmacy",
                FormularyTier = "Tier 1",
                IsSpecialtyDrug = false,
                DaysSupply = 30,
            },
            new BehavioralHealthBenefit
            {
                ServiceCategory = "Mental Health",
                ParityCategory = "OutpatientInNetwork",
            },
            new PreventiveBenefit
            {
                ServiceCategory = "Preventive",
                IsAcaPreventive = true,
                UspstfRecommendationGrade = "A",
            },
        }
    };

    [Fact]
    public async Task Round_trip_preserves_subclass_identity_through_repository()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(PlanWithTypedBenefits(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var reloaded = await service.GetVersionAsync(v1.PlanId, v1.VersionId, Tenant);

        reloaded.Should().NotBeNull();
        reloaded!.Benefits.Should().HaveCount(4);
        reloaded.Benefits[0].Should().BeOfType<MedicalBenefit>();
        reloaded.Benefits[1].Should().BeOfType<PharmacyBenefit>();
        reloaded.Benefits[2].Should().BeOfType<BehavioralHealthBenefit>();
        reloaded.Benefits[3].Should().BeOfType<PreventiveBenefit>();

        ((PharmacyBenefit)reloaded.Benefits[1]).FormularyTier.Should().Be("Tier 1");
        ((BehavioralHealthBenefit)reloaded.Benefits[2]).IsParityProtected.Should().BeTrue();
        ((PreventiveBenefit)reloaded.Benefits[3]).UspstfRecommendationGrade.Should().Be("A");
    }

    [Fact]
    public async Task AmendPublishedPlan_clones_typed_benefits_into_v2_draft()
    {
        var (service, repo, _, _) = Build();

        var draft = await service.CreateDraftAsync(PlanWithTypedBenefits(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var v2Draft = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, Actor);

        v2Draft.Benefits.Should().HaveCount(4);
        v2Draft.Benefits[1].Should().BeOfType<PharmacyBenefit>(
            "the amend flow uses CloneBenefit which JSON-round-trips the polymorphic shape");
        ((PharmacyBenefit)v2Draft.Benefits[1]).FormularyTier.Should().Be("Tier 1");

        // mutate the typed facet on v2 only and publish
        var pharmacy = (PharmacyBenefit)v2Draft.Benefits[1];
        pharmacy.FormularyTier = "Tier 2";
        await repo.UpdateDraftAsync(v2Draft);
        var v2 = await service.PublishVersionAsync(v2Draft.PlanId, v2Draft.VersionId, Tenant, Actor);

        // v1 still has Tier 1, v2 has Tier 2 — version isolation holds for typed facets.
        var v1Reloaded = await service.GetVersionAsync(v1.PlanId, v1.VersionId, Tenant);
        ((PharmacyBenefit)v1Reloaded!.Benefits[1]).FormularyTier.Should().Be("Tier 1");
        ((PharmacyBenefit)v2.Benefits[1]).FormularyTier.Should().Be("Tier 2");
    }

    [Fact]
    public async Task Repository_returned_benefit_is_independent_of_stored_instance()
    {
        // The fake clones on read; mutating a returned typed benefit must
        // not bleed into the stored document.
        var (_, repo, _, _) = Build();
        var plan = PlanWithTypedBenefits();
        plan.VersionId = "vid";
        plan.VersionNumber = 1;
        plan.VersionState = PlanVersionState.Published;
        await repo.CreateAsync(plan);

        var read = await repo.GetByIdAsync(plan.Id, Tenant);
        ((PharmacyBenefit)read!.Benefits[1]).FormularyTier = "MUTATED";

        var reread = await repo.GetByIdAsync(plan.Id, Tenant);
        ((PharmacyBenefit)reread!.Benefits[1]).FormularyTier.Should().Be("Tier 1");
    }
}
