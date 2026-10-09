using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using EngineCobDeductibleCredit = CloudHealthOffice.BenefitEngine.Domain.CobDeductibleCredit;
using ModelCobDeductibleCredit = BenefitPlanService.Models.CobDeductibleCredit;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// The per-plan COB deductible-credit setting (<c>cobDeductibleCredit</c>):
/// defaults to NAIC full credit (legacy documents included), round-trips by
/// name, is accepted by plan validation, and reaches the engine config.
/// </summary>
public sealed class CobDeductibleCreditPlanSettingTests
{
    private static BenefitPlan Plan(ModelCobDeductibleCredit? credit = null)
    {
        var plan = new BenefitPlan
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = "tenant-a",
            PlanId = "plan-001",
            VersionId = "v1",
            PlanName = "Test",
            PlanType = PlanType.PPO,
            EffectiveDate = new DateTime(2025, 1, 1),
            VersionState = PlanVersionState.Published,
            CostSharing = new CostSharing { IndividualOutOfPocketMax = 8_000m, FamilyOutOfPocketMax = 16_000m },
            Benefits = new(),
        };
        if (credit is { } c) plan.CobDeductibleCredit = c;
        return plan;
    }

    private static IPlanLimitValidator Validator() => new PlanLimitValidator(
        new StubLimits(new AcaLimits(2025, 9_200m, 18_400m)),
        new PlanYearResolver(),
        NullLogger<PlanLimitValidator>.Instance);

    private static ChoBenefitPlanProvider Provider() => new(
        new InMemoryBenefitPlanRepository(),
        new StubTenantContext("tenant-a"),
        new StubLimits(new AcaLimits(2025, 9_200m, 18_400m)),
        new PlanYearResolver(),
        new MemoryCache(Options.Create(new MemoryCacheOptions())),
        NullLogger<ChoBenefitPlanProvider>.Instance);

    [Fact]
    public void Default_IsNaicFullCredit()
    {
        new BenefitPlan().CobDeductibleCredit.Should().Be(ModelCobDeductibleCredit.NaicFullCredit);
    }

    [Fact]
    public void LegacyDocumentWithoutTheField_HydratesAsNaicFullCredit()
    {
        var plan = JsonSerializer.Deserialize<BenefitPlan>(
            """{ "planId": "legacy", "planName": "Legacy plan" }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        plan.CobDeductibleCredit.Should().Be(ModelCobDeductibleCredit.NaicFullCredit);
    }

    [Theory]
    [InlineData(ModelCobDeductibleCredit.NaicFullCredit, "NaicFullCredit")]
    [InlineData(ModelCobDeductibleCredit.MemberPaidOnly, "MemberPaidOnly")]
    [InlineData(ModelCobDeductibleCredit.NoDeductible, "NoDeductible")]
    public void SerializesByName_AndRoundTrips(ModelCobDeductibleCredit credit, string name)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Plan(credit), options);

        json.Should().Contain($"\"cobDeductibleCredit\":\"{name}\"");
        JsonSerializer.Deserialize<BenefitPlan>(json, options)!.CobDeductibleCredit.Should().Be(credit);
    }

    [Theory]
    [InlineData(ModelCobDeductibleCredit.NaicFullCredit)]
    [InlineData(ModelCobDeductibleCredit.MemberPaidOnly)]
    [InlineData(ModelCobDeductibleCredit.NoDeductible)]
    public void Validation_AcceptsEverySetting(ModelCobDeductibleCredit credit)
    {
        var act = () => Validator().Validate(Plan(credit), PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validation_RejectsAnUndefinedValue()
    {
        var act = () => Validator().Validate(Plan((ModelCobDeductibleCredit)42), PlanLimitWriteCaller.CreatePlan);

        act.Should().Throw<PlanLimitValidationException>()
            .Which.Field.Should().Be("cobDeductibleCredit");
    }

    [Theory]
    [InlineData(ModelCobDeductibleCredit.NaicFullCredit, EngineCobDeductibleCredit.NaicFullCredit)]
    [InlineData(ModelCobDeductibleCredit.MemberPaidOnly, EngineCobDeductibleCredit.MemberPaidOnly)]
    [InlineData(ModelCobDeductibleCredit.NoDeductible, EngineCobDeductibleCredit.NoDeductible)]
    public void MapToConfig_ProjectsTheSetting(ModelCobDeductibleCredit credit, EngineCobDeductibleCredit expected)
    {
        Provider().MapToConfig(Plan(credit)).CobDeductibleCredit.Should().Be(expected);
    }

    private sealed class StubTenantContext(string tenantId) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = tenantId;
    }

    private sealed class StubLimits(AcaLimits limits) : IAcaLimitsProvider
    {
        public AcaLimits? GetForPlanYear(int planYear) => planYear == limits.PlanYear ? limits : null;
        public IReadOnlyCollection<int> ConfiguredPlanYears => [limits.PlanYear];
    }
}
