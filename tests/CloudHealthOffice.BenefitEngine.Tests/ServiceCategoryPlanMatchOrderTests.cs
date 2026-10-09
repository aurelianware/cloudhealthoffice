using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// How a resolved service category finds its plan category
/// (<see cref="BenefitPlanConfig.LookupCategories"/>): exact name, then the
/// specific X12 code the resolver used (<see cref="ServiceCategoryMatch.X12Code"/>),
/// then the name's other X12 codes, then — only when nothing matched — the
/// rollout fallback chain (<see cref="ServiceCategoryNames.RolloutFallbackFor"/>).
/// </summary>
public class ServiceCategoryPlanMatchOrderTests
{
    // ---- Specific X12 code ahead of the name's other codes ----------------

    [Theory]
    [InlineData("55", "AI")] // residential substance abuse
    [InlineData("57", "AI")] // non-residential substance abuse
    [InlineData("58", "AI")] // opioid treatment
    [InlineData("53", "MH")] // community mental health center
    [InlineData("51", "A4")] // inpatient psychiatric
    public async Task Resolver_CarriesTheX12CodeItUsed(string pos, string x12)
    {
        var match = await Resolver().ResolveAsync("t", Guid.NewGuid(), new DateOnly(2026, 3, 8),
            "ZZZZZ", "CPT", pos, [], null, new ServiceCategoryClaimContext("837P", null));

        Assert.NotNull(match);
        Assert.Equal(ServiceCategoryNames.BehavioralHealth, match!.ServiceTypeCode);
        Assert.Equal(x12, match.X12Code);
    }

    [Fact]
    public void GetCategories_PrefersTheSpecificX12Code_OverTheNamesPrimaryCode()
    {
        var plan = Plan(Category("A4", copay: 50m), Category("MH", copay: 30m), Category("AI", copay: 10m));

        Assert.Equal("AI", Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth, "AI")).ServiceTypeCode);
        Assert.Equal("MH", Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth, "MH")).ServiceTypeCode);
        // Without a specific code the primary (A4) is still first.
        Assert.Equal("A4", Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth)).ServiceTypeCode);
    }

    [Fact]
    public void GetCategories_ExactName_StillBeatsTheSpecificX12Code()
    {
        var plan = Plan(Category(ServiceCategoryNames.BehavioralHealth), Category("AI"));

        Assert.Equal(ServiceCategoryNames.BehavioralHealth,
            Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth, "AI")).ServiceTypeCode);
    }

    [Fact]
    public void GetCategories_SpecificCodeMissing_FallsBackToTheNamesOtherCodes()
    {
        var plan = Plan(Category("A4"));

        Assert.Equal("A4", Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth, "AI")).ServiceTypeCode);
    }

    [Theory]
    [InlineData("55", 10)] // AI substance abuse, not A4 psychiatric
    [InlineData("53", 30)] // MH mental health
    [InlineData("51", 50)] // A4 psychiatric
    public async Task Engine_PlanWithSeparateBehavioralHealthCodes_PaysTheSpecificOne(string pos, int copay)
    {
        var plan = Plan(Category("A4", copay: 50m), Category("MH", copay: 30m), Category("AI", copay: 10m));

        var line = await Adjudicate(plan, "ZZZZZ", pos);

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(copay, line.CopayAmount);
    }

    // ---- Rollout fallback chain -------------------------------------------

    [Fact]
    public void RolloutFallbacks_AreExactlyTheVerifiedChain()
    {
        var expected = new Dictionary<string, string>
        {
            [ServiceCategoryNames.UrgentCare] = ServiceCategoryNames.OfficeVisit,
            [ServiceCategoryNames.OutpatientSurgery] = ServiceCategoryNames.OutpatientHospital,
            [ServiceCategoryNames.Laboratory] = ServiceCategoryNames.OutpatientHospital,
            [ServiceCategoryNames.Hospice] = ServiceCategoryNames.HomeHealth,
        };

        Assert.Equal(expected.OrderBy(kv => kv.Key), ServiceCategoryNames.RolloutFallbacks.OrderBy(kv => kv.Key));
        // Deliberately absent (see ServiceCategoryNames.RolloutFallbackByName).
        Assert.Null(ServiceCategoryNames.RolloutFallbackFor(ServiceCategoryNames.PhysicalTherapy));
        Assert.Null(ServiceCategoryNames.RolloutFallbackFor(ServiceCategoryNames.SkilledNursing));
    }

    [Theory]
    [InlineData("20", ServiceCategoryNames.OfficeVisit)]        // Urgent Care
    [InlineData("24", ServiceCategoryNames.OutpatientHospital)] // Outpatient Surgery
    [InlineData("81", ServiceCategoryNames.OutpatientHospital)] // Laboratory
    [InlineData("34", ServiceCategoryNames.HomeHealth)]         // Hospice
    public async Task Engine_PlanWithoutTheNewCategory_PaysUnderTheFallback_AndWarns(string pos, string fallback)
    {
        var plan = Plan(Category(ServiceCategoryNames.InpatientHospital, copay: 500m), Category(fallback, copay: 15m));
        var gateLogger = new CapturingLogger<BenefitRuleGate>();

        var line = await Adjudicate(plan, "ZZZZZ", pos, gateLogger);

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(15m, line.CopayAmount);
        var warning = Assert.Single(gateLogger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(fallback, warning.Message);
    }

    [Fact]
    public async Task Engine_PlanWithTheSpecificCategory_NeverUsesTheFallback()
    {
        var plan = Plan(Category(ServiceCategoryNames.UrgentCare, copay: 75m), Category(ServiceCategoryNames.OfficeVisit, copay: 15m));
        var gateLogger = new CapturingLogger<BenefitRuleGate>();

        var line = await Adjudicate(plan, "ZZZZZ", "20", gateLogger);

        Assert.Equal(75m, line.CopayAmount);
        Assert.DoesNotContain(gateLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void GetCategories_Fallback_MatchesAnX12KeyedTarget()
    {
        var plan = Plan(Category("98"));

        var lookup = plan.LookupCategories(ServiceCategoryNames.UrgentCare, "UC");

        Assert.Equal("98", Assert.Single(lookup.Categories).ServiceTypeCode);
        Assert.Equal(ServiceCategoryNames.OfficeVisit, lookup.FallbackCategory);
    }

    [Fact]
    public void GetCategories_Fallback_IsNotChained()
    {
        // Laboratory → Outpatient Hospital; Outpatient Hospital has no
        // fallback of its own, so a plan with neither gets nothing.
        var plan = Plan(Category(ServiceCategoryNames.InpatientHospital));

        Assert.Empty(plan.GetCategories(ServiceCategoryNames.Laboratory));
    }

    [Fact]
    public async Task Engine_SkilledNursing_HasNoFallback_DeniesWithCarc96()
    {
        var plan = Plan(Category(ServiceCategoryNames.InpatientHospital, copay: 500m));

        var line = await Adjudicate(plan, "ZZZZZ", "31");

        Assert.False(line.IsCovered);
        Assert.Equal("96", line.DenialReasonCode);
    }

    [Fact]
    public async Task Engine_Pos54_StaysUnmapped_DeniesWithCarc204()
    {
        var plan = Plan(Category(ServiceCategoryNames.InpatientHospital), Category(ServiceCategoryNames.OfficeVisit));

        var line = await Adjudicate(plan, "ZZZZZ", "54");

        Assert.Equal("204", line.DenialReasonCode);
    }

    // ---- ServiceCategoryNames surface ---------------------------------------

    [Fact]
    public void ServiceCategoryNames_HasNoUnusedKnownX12CodesProperty()
    {
        Assert.Null(typeof(ServiceCategoryNames).GetProperty("KnownX12Codes"));
    }

    // ---- helpers ------------------------------------------------------------

    private static ServiceCategoryResolver Resolver() =>
        new(new EmptyRepo(), NullLogger<ServiceCategoryResolver>.Instance);

    private static BenefitCategoryConfig Category(string code, decimal copay = 0m) => new()
    {
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        IsCovered = true,
        InNetworkCostSharing = [new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = copay }],
    };

    private static BenefitPlanConfig Plan(params BenefitCategoryConfig[] categories) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        PlanName = "Match order plan",
        PlanType = PlanType.PPO,
        PlanYear = "2026",
        IndividualDeductible = 0,
        FamilyDeductible = 0,
        IndividualOopMax = 10000,
        FamilyOopMax = 20000,
        Categories = [.. categories],
    };

    private static async Task<LineBenefitResult> Adjudicate(
        BenefitPlanConfig plan, string procedureCode, string pos, ILogger<BenefitRuleGate>? gateLogger = null)
    {
        var engine = new BenefitCalculationEngine(
            Resolver(),
            new InMemoryBenefitPlanProvider(plan),
            new InMemoryAccumulatorService(plan, 0, 0, 0, ServiceCategoryNames.OfficeVisit),
            new BenefitRuleGate(gateLogger ?? NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        var result = await engine.CalculateAsync(new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = plan.Id,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = NetworkTier.InNetwork,
            ClaimType = "837P",
            ClaimId = Guid.NewGuid().ToString(),
            Lines = [new ClaimLineInput { LineNumber = 1, ProcedureCode = procedureCode, PlaceOfService = pos, BilledAmount = 200m, Units = 1 }],
            AllowedAmounts = new Dictionary<int, decimal> { [1] = 100m },
        });

        return Assert.Single(result.Lines);
    }

    private sealed class EmptyRepo : IServiceCategoryMappingRepository
    {
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServiceCategoryMapping>>(Array.Empty<ServiceCategoryMapping>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
