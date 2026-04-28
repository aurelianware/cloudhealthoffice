using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
