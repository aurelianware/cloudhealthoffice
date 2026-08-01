using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BenefitRulePredicate = CloudHealthOffice.BenefitEngine.Domain.BenefitRulePredicate;
using EngineFamilyAccumulatorModel = CloudHealthOffice.BenefitEngine.Domain.FamilyAccumulatorModel;
using ModelFamilyAccumulatorModel = BenefitPlanService.Models.FamilyAccumulatorModel;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.7 — verifies <see cref="ChoBenefitPlanProvider.MapToConfig"/>
/// projects the new <c>FamilyAccumulatorModel</c> field plus the ACA
/// individual cap and the gated <c>IsAcaCapEnforced</c> flag onto the
/// engine config.
/// </summary>
public sealed class ChoBenefitPlanProviderModelMappingTests
{
    private static ChoBenefitPlanProvider Build(IAcaLimitsProvider? limits = null,
        IBenefitPlanRepository? repo = null)
    {
        repo ??= new InMemoryBenefitPlanRepository();
        limits ??= new StubLimits(new AcaLimits(2025, 9_200m, 18_400m));
        return new ChoBenefitPlanProvider(
            repo,
            new StubTenantContext("tenant-a"),
            limits,
            new PlanYearResolver(),
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            NullLogger<ChoBenefitPlanProvider>.Instance);
    }

    private static BenefitPlan SamplePlan(
        ModelFamilyAccumulatorModel model = ModelFamilyAccumulatorModel.Embedded,
        DateTime? publishedAt = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "tenant-a",
        PlanId = "plan-001",
        PlanName = "Test",
        PlanType = PlanType.PPO,
        EffectiveDate = new DateTime(2025, 1, 1),
        VersionState = PlanVersionState.Published,
        PublishedAt = publishedAt,
        FamilyAccumulatorModel = model,
        CostSharing = new CostSharing
        {
            IndividualOutOfPocketMax = 8_000m,
            FamilyOutOfPocketMax = 16_000m,
        },
        Benefits = new(),
    };

    [Fact]
    public void MapToConfig_Propagates_FamilyAccumulatorModel_Aggregate()
    {
        var provider = Build();
        var plan = SamplePlan(model: ModelFamilyAccumulatorModel.Aggregate);

        var config = provider.MapToConfig(plan);

        config.FamilyAccumulatorModel.Should().Be(EngineFamilyAccumulatorModel.Aggregate);
    }

    [Fact]
    public void MapToConfig_Defaults_To_Embedded_For_Legacy_Plans()
    {
        var provider = Build();
        var plan = SamplePlan();

        var config = provider.MapToConfig(plan);

        config.FamilyAccumulatorModel.Should().Be(EngineFamilyAccumulatorModel.Embedded);
    }

    [Fact]
    public void MapToConfig_Carries_AcaIndividualCap_From_LimitsProvider()
    {
        var provider = Build();
        var plan = SamplePlan(model: ModelFamilyAccumulatorModel.Aggregate);

        var config = provider.MapToConfig(plan);

        config.AcaIndividualCap.Should().Be(9_200m);
    }

    [Fact]
    public void MapToConfig_Returns_Null_AcaCap_When_PlanYear_Not_Configured()
    {
        var provider = Build(limits: new StubLimits(/* nothing for 2025 */));
        var plan = SamplePlan(model: ModelFamilyAccumulatorModel.Aggregate);

        var config = provider.MapToConfig(plan);

        config.AcaIndividualCap.Should().BeNull();
    }

    [Fact]
    public void MapToConfig_Sets_IsAcaCapEnforced_True_For_Post_Cutoff_Aggregate_Plans()
    {
        var provider = Build();
        var plan = SamplePlan(
            model: ModelFamilyAccumulatorModel.Aggregate,
            publishedAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var config = provider.MapToConfig(plan);

        config.IsAcaCapEnforced.Should().BeTrue();
    }

    [Fact]
    public void MapToConfig_Sets_IsAcaCapEnforced_False_For_Legacy_Aggregate_Plans()
    {
        var provider = Build();
        var plan = SamplePlan(
            model: ModelFamilyAccumulatorModel.Aggregate,
            publishedAt: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var config = provider.MapToConfig(plan);

        config.IsAcaCapEnforced.Should().BeFalse();
    }

    [Fact]
    public void MapToConfig_Projects_TwoBenefitsSameServiceCategory_AsTwoEntries()
    {
        // BP 5.10 — projection no longer deduplicates by ServiceCategory.
        // The rule gate consumes the duplicates and picks one per encounter.
        var provider = Build();
        var plan = SamplePlan();
        plan.Benefits.Add(new MedicalBenefit { ServiceCategory = "98", Description = "Pediatric Office Visit" });
        plan.Benefits.Add(new MedicalBenefit { ServiceCategory = "98", Description = "Adult Office Visit" });

        var config = provider.MapToConfig(plan);

        var pediatric = config.Categories.FirstOrDefault(c => c.ServiceTypeDescription == "Pediatric Office Visit");
        var adult = config.Categories.FirstOrDefault(c => c.ServiceTypeDescription == "Adult Office Visit");
        pediatric.Should().NotBeNull();
        adult.Should().NotBeNull();
        config.GetCategories("98").Should().HaveCount(2);
    }

    [Fact]
    public void MapToConfig_Projects_ExplicitExclusion_AsUncoveredCategory()
    {
        var provider = Build();
        var plan = SamplePlan();
        plan.Benefits.Add(new MedicalBenefit
        {
            ServiceCategory = "COSMETIC",
            Description = "Cosmetic Procedures",
            IsCovered = false,
        });

        var config = provider.MapToConfig(plan);

        var exclusion = config.GetCategories("COSMETIC").Should().ContainSingle().Subject;
        exclusion.IsCovered.Should().BeFalse();
        exclusion.ServiceTypeDescription.Should().Be("Cosmetic Procedures");
    }

    [Fact]
    public void MapToConfig_Carries_Predicate_From_NonEmpty_Rules()
    {
        var provider = Build();
        var plan = SamplePlan();
        plan.Benefits.Add(new MedicalBenefit
        {
            ServiceCategory = "98",
            Description = "Pediatric Office Visit",
            Rules = new List<BenefitRulePredicate>
            {
                new() { MemberAgeMin = 0, MemberAgeMax = 17 },
            },
        });

        var config = provider.MapToConfig(plan);

        var category = config.GetCategories("98").Single();
        category.Predicate.Should().NotBeNull();
        category.Predicate!.MemberAgeMax.Should().Be(17);
    }

    [Fact]
    public void MapToConfig_NullRules_LeavesPredicateNull()
    {
        var provider = Build();
        var plan = SamplePlan();
        plan.Benefits.Add(new MedicalBenefit
        {
            ServiceCategory = "98",
            Description = "Office Visit",
            Rules = null,
        });

        var config = provider.MapToConfig(plan);

        config.GetCategories("98").Single().Predicate.Should().BeNull();
    }

    [Fact]
    public void MapToConfig_MultiplePredicates_TruncatesToFirst_AndPreservesOrder()
    {
        // Decision 4 — multi-predicate AND semantics is Phase 2; the
        // projection collapses to the first non-null entry.
        var provider = Build();
        var plan = SamplePlan();
        var first = new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 };
        var second = new BenefitRulePredicate { RequiredDiagnosisCodes = new() { "Z00.00" } };
        plan.Benefits.Add(new MedicalBenefit
        {
            ServiceCategory = "98",
            Description = "Pediatric Wellness",
            Rules = new List<BenefitRulePredicate> { first, second },
        });

        var config = provider.MapToConfig(plan);

        var category = config.GetCategories("98").Single();
        category.Predicate.Should().NotBeNull();
        category.Predicate!.MemberAgeMax.Should().Be(17, "first predicate is preserved; second is truncated");
        category.Predicate.RequiredDiagnosisCodes.Should().BeNull();
    }

    [Fact]
    public void MapToConfig_Sets_IsAcaCapEnforced_False_For_Embedded_Plans()
    {
        // Embedded plans never need runtime enforcement (the existing
        // IndividualOutOfPocketMax constraint is enforced by the engine
        // separately). Field stays false regardless of publish date.
        var provider = Build();
        var plan = SamplePlan(
            model: ModelFamilyAccumulatorModel.Embedded,
            publishedAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var config = provider.MapToConfig(plan);

        config.IsAcaCapEnforced.Should().BeFalse();
    }

    private sealed class StubTenantContext : IBenefitEngineTenantContext
    {
        public StubTenantContext(string tenantId) { TenantId = tenantId; }
        public string TenantId { get; }
    }

    private sealed class StubLimits : IAcaLimitsProvider
    {
        private readonly Dictionary<int, AcaLimits> _byYear;
        public StubLimits(params AcaLimits[] rows)
        {
            _byYear = rows.ToDictionary(r => r.PlanYear);
        }
        public AcaLimits? GetForPlanYear(int planYear)
            => _byYear.TryGetValue(planYear, out var row) ? row : null;
        public IReadOnlyCollection<int> ConfiguredPlanYears => _byYear.Keys;
    }
}
