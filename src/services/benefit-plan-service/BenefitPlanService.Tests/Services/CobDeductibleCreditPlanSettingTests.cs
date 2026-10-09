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

    // ── M3: GET → PUT round trip keeps the setting ─────────────────────

    [Theory]
    [InlineData(ModelCobDeductibleCredit.MemberPaidOnly)]
    [InlineData(ModelCobDeductibleCredit.NoDeductible)]
    public void GetThenPut_RoundTrip_KeepsTheSetting(ModelCobDeductibleCredit credit)
    {
        // GET: the adapter projection the controller returns, as JSON.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var getBody = JsonSerializer.Serialize(
            BenefitPlanService.Models.AdapterBenefitPlan.From(Plan(credit)).ToBenefitPlan(), options);

        // PUT: the client sends the document back unchanged.
        var putPlan = JsonSerializer.Deserialize<BenefitPlan>(getBody, options)!;

        putPlan.CobDeductibleCredit.Should().Be(credit);
        BenefitPlanService.Models.AdapterBenefitPlan.From(putPlan).CobDeductibleCredit.Should().Be(credit);
    }

    // ── HDHP: the deductible cannot be skipped ─────────────────────────

    [Fact]
    public void Validation_RejectsNoDeductible_OnAnHdhp()
    {
        var plan = Plan(ModelCobDeductibleCredit.NoDeductible);
        plan.PlanType = PlanType.HDHP;

        var act = () => Validator().Validate(plan, PlanLimitWriteCaller.CreatePlan);

        act.Should().Throw<PlanLimitValidationException>()
            .Which.Field.Should().Be("cobDeductibleCredit");
    }

    [Theory]
    [InlineData(ModelCobDeductibleCredit.NaicFullCredit)]
    [InlineData(ModelCobDeductibleCredit.MemberPaidOnly)]
    public void Validation_AcceptsTheOtherSettings_OnAnHdhp(ModelCobDeductibleCredit credit)
    {
        var plan = Plan(credit);
        plan.PlanType = PlanType.HDHP;

        var act = () => Validator().Validate(plan, PlanLimitWriteCaller.CreatePlan);

        act.Should().NotThrow();
    }

    // ── API: a later payer needs the prior payers ──────────────────────

    private static CloudHealthOffice.CobEngine.Domain.PriorPayerAdjudication Prior(int sequence) =>
        new() { Sequence = sequence, ClaimPaidAmount = 10m };

    [Fact]
    public void CobRequest_PrimaryNeedsNothing() =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(1, [], 0).Should().BeNull();

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CobRequest_LaterPayerWithoutPriorPayers_IsRejected(int sequence) =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(sequence, [], 0)
            .Should().Contain("requires cob.priorPayers");

    [Fact]
    public void CobRequest_TertiaryWithOnlyLaterPayers_IsRejected() =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(3, [Prior(3), Prior(4)], 0)
            .Should().NotBeNull();

    [Fact]
    public void CobRequest_DuplicateSequences_AreRejected() =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(3, [Prior(1), Prior(1)], 0)
            .Should().Contain("more than once");

    [Fact]
    public void CobRequest_LegacySecondaryPrimaryPaymentByLine_StillAccepted() =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(2, [], legacyPrimaryLineCount: 2).Should().BeNull();

    [Theory]
    [InlineData(3, new[] { 1 }, "2")]
    [InlineData(3, new[] { 2 }, "1")]
    [InlineData(4, new[] { 1, 3 }, "2")]
    public void CobRequest_MissingAnEarlierSequence_IsRejected(int sequence, int[] present, string missing) =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(sequence, present.Select(Prior).ToList(), 0)
            .Should().Contain($"missing sequence {missing}");

    [Fact]
    public void CobRequest_TertiaryWithBothPriorPayers_IsValid() =>
        BenefitPlanService.Controllers.CobRequestValidation.Validate(3, [Prior(1), Prior(2)], 0).Should().BeNull();

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
