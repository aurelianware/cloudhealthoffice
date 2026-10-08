using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelPlanType = BenefitPlanService.Models.PlanType;
using NetworkTier = CloudHealthOffice.BenefitEngine.Domain.NetworkTier;
using ModelFamilyAccumulatorModel = BenefitPlanService.Models.FamilyAccumulatorModel;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Seam test: a stored <see cref="BenefitPlan"/> goes through the
/// production <see cref="ChoBenefitPlanProvider"/> projection and into
/// the real <see cref="BenefitCalculationEngine"/>. Engine unit tests
/// hand-build <see cref="BenefitPlanConfig"/>, so they cannot catch a
/// projection that drops the deductible signal (non-HDHP plans paid
/// every claim as if the deductible were already met).
/// </summary>
public sealed class ChoBenefitPlanProviderEngineSeamTests
{
    private const string TenantId = "tenant-a";
    private const string OfficeVisit = "98";

    private static BenefitPlan StoredPlan(bool deductibleApplies) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        PlanId = "plan-seam",
        PlanName = "Seam PPO",
        PlanType = ModelPlanType.PPO,
        EffectiveDate = new DateTime(2025, 1, 1),
        VersionState = PlanVersionState.Published,
        CostSharing = new CostSharing
        {
            IndividualDeductible = 500m,
            FamilyDeductible = 1_500m,
            IndividualOutOfPocketMax = 3_000m,
            FamilyOutOfPocketMax = 9_000m,
            OutNetworkIndividualDeductible = 1_000m,
            OutNetworkFamilyDeductible = 3_000m,
            OutNetworkIndividualOutOfPocketMax = 6_000m,
            OutNetworkFamilyOutOfPocketMax = 12_000m,
        },
        Benefits = new()
        {
            new MedicalBenefit
            {
                ServiceCategory = OfficeVisit,
                Description = "Office Visit",
                InNetworkCopay = 30m,
                InNetworkCoinsurance = 0.20m,
                OutNetworkCoinsurance = 0.40m,
                DeductibleApplies = deductibleApplies,
            },
        },
    };

    private static async Task<BenefitResolutionResult> AdjudicateAsync(
        BenefitPlan stored, NetworkTier tier, decimal allowed, string serviceTypeCode = OfficeVisit)
    {
        var repo = new InMemoryBenefitPlanRepository();
        await repo.CreateAsync(stored);

        var provider = new ChoBenefitPlanProvider(
            repo,
            new StubTenantContext(TenantId),
            new StubLimits(new AcaLimits(2025, 9_200m, 18_400m)),
            new PlanYearResolver(),
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            NullLogger<ChoBenefitPlanProvider>.Instance);

        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver(serviceTypeCode),
            provider,
            new EmptyAccumulatorService(),
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        return await engine.CalculateAsync(new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = Guid.Parse(stored.Id),
            ServiceDate = new DateOnly(2025, 3, 8),
            NetworkTier = tier,
            ClaimId = Guid.NewGuid().ToString(),
            Lines = new()
            {
                new ClaimLineInput
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    PlaceOfService = "11",
                    BilledAmount = allowed,
                },
            },
            AllowedAmounts = new() { [1] = allowed },
        });
    }

    [Fact]
    public async Task InNetwork_DeductibleApplies_DeductibleTakenBeforeCopayAndCoinsurance()
    {
        var result = await AdjudicateAsync(StoredPlan(deductibleApplies: true), NetworkTier.InNetwork, 800m);

        result.Success.Should().BeTrue();
        var line = result.Lines.Single();
        line.DeductibleAmount.Should().Be(500m);
        line.CopayAmount.Should().Be(30m);
        line.CoinsuranceAmount.Should().Be(54m); // 20% of (800 - 500 - 30)
        line.MemberResponsibility.Should().Be(584m);
        line.PlanPaidAmount.Should().Be(216m);
        line.Adjustments.Should().Contain(a => a.GroupCode == "PR" && a.ReasonCode == "1" && a.Amount == 500m);
    }

    [Fact]
    public async Task OutOfNetwork_DeductibleApplies_OonDeductibleTakenBeforeCoinsurance()
    {
        var result = await AdjudicateAsync(StoredPlan(deductibleApplies: true), NetworkTier.OutOfNetwork, 1_500m);

        result.Success.Should().BeTrue();
        var line = result.Lines.Single();
        line.DeductibleAmount.Should().Be(1_000m);
        line.CopayAmount.Should().Be(0m);
        line.CoinsuranceAmount.Should().Be(200m); // 40% of (1500 - 1000)
        line.MemberResponsibility.Should().Be(1_200m);
        line.PlanPaidAmount.Should().Be(300m);
    }

    [Fact]
    public async Task InNetwork_DeductibleDoesNotApply_CopayInsteadOfDeductible()
    {
        var result = await AdjudicateAsync(StoredPlan(deductibleApplies: false), NetworkTier.InNetwork, 800m);

        var line = result.Lines.Single();
        line.DeductibleAmount.Should().Be(0m);
        line.CopayAmount.Should().Be(30m);
        line.CoinsuranceAmount.Should().Be(154m); // 20% of (800 - 30)
        line.MemberResponsibility.Should().Be(184m);
        line.PlanPaidAmount.Should().Be(616m);
    }

    [Fact]
    public async Task OutOfNetwork_DeductibleDoesNotApply_CoinsuranceOnly()
    {
        var result = await AdjudicateAsync(StoredPlan(deductibleApplies: false), NetworkTier.OutOfNetwork, 1_500m);

        var line = result.Lines.Single();
        line.DeductibleAmount.Should().Be(0m);
        line.CoinsuranceAmount.Should().Be(600m);
        line.MemberResponsibility.Should().Be(600m);
        line.PlanPaidAmount.Should().Be(900m);
    }

    // ── Unset family limits ────────────────────────────────────────────

    private static BenefitPlan Plan(
        CostSharing costSharing,
        ModelPlanType planType = ModelPlanType.PPO,
        ModelFamilyAccumulatorModel model = ModelFamilyAccumulatorModel.Embedded,
        DateTime? publishedAt = null,
        params Benefit[] benefits) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        PlanId = "plan-seam",
        PlanName = "Seam",
        PlanType = planType,
        EffectiveDate = new DateTime(2025, 1, 1),
        VersionState = PlanVersionState.Published,
        PublishedAt = publishedAt,
        FamilyAccumulatorModel = model,
        CostSharing = costSharing,
        Benefits = benefits.ToList(),
    };

    private static MedicalBenefit OfficeVisitBenefit(bool deductibleApplies = true) => new()
    {
        ServiceCategory = OfficeVisit,
        Description = "Office Visit",
        InNetworkCopay = 30m,
        InNetworkCoinsurance = 0.20m,
        OutNetworkCoinsurance = 0.40m,
        DeductibleApplies = deductibleApplies,
    };

    [Fact]
    public async Task Embedded_NoFamilyLimits_MemberStillOwesDeductibleAndCostShare()
    {
        // Single-coverage style plan: no family deductible / OOP max. Those
        // used to seed zero-limit family accumulators that read as "met",
        // so the member owed nothing.
        var stored = Plan(
            new CostSharing { IndividualDeductible = 500m, IndividualOutOfPocketMax = 3_000m },
            benefits: OfficeVisitBenefit());

        var line = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 800m)).Lines.Single();

        line.DeductibleAmount.Should().Be(500m);
        line.MemberResponsibility.Should().Be(584m);
        line.PlanPaidAmount.Should().Be(216m);
    }

    [Fact]
    public async Task Aggregate_NoFamilyOopMax_MemberOwesCostShare_AcaCapStillBounds()
    {
        var stored = Plan(
            new CostSharing { FamilyDeductible = 1_000m },
            model: ModelFamilyAccumulatorModel.Aggregate,
            publishedAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            benefits: OfficeVisitBenefit());

        var small = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 800m)).Lines.Single();
        small.DeductibleAmount.Should().Be(800m);
        small.MemberResponsibility.Should().Be(800m);

        // 1000 + 30 + 20% of 48,970 = 10,824 raw; ACA 2025 cap is 9,200.
        var large = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 50_000m)).Lines.Single();
        large.MemberResponsibility.Should().Be(9_200m);
    }

    // ── ACA preventive ─────────────────────────────────────────────────

    private static CostSharing StandardCostSharing() => new()
    {
        IndividualDeductible = 500m,
        FamilyDeductible = 1_500m,
        IndividualOutOfPocketMax = 3_000m,
        FamilyOutOfPocketMax = 9_000m,
        OutNetworkIndividualDeductible = 1_000m,
        OutNetworkFamilyDeductible = 3_000m,
        OutNetworkIndividualOutOfPocketMax = 6_000m,
        OutNetworkFamilyOutOfPocketMax = 12_000m,
    };

    [Fact]
    public async Task PreventiveCategory_InNetwork_NoCostShare_DespiteDeductibleDefault()
    {
        var preventive = new MedicalBenefit
        {
            ServiceCategory = "Preventive",
            Description = "Preventive Care",
            InNetworkCopay = 20m,
            InNetworkCoinsurance = 0.20m,
            OutNetworkCoinsurance = 0.40m,
            // DeductibleApplies left at its default (true).
        };
        var stored = Plan(StandardCostSharing(), benefits: preventive);

        var inn = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 200m, "Preventive")).Lines.Single();
        inn.MemberResponsibility.Should().Be(0m);
        inn.PlanPaidAmount.Should().Be(200m);

        // Out of network the mandate does not apply: normal cost sharing.
        var oon = (await AdjudicateAsync(stored, NetworkTier.OutOfNetwork, 1_500m, "Preventive")).Lines.Single();
        oon.DeductibleAmount.Should().Be(1_000m);
        oon.MemberResponsibility.Should().Be(1_200m);
    }

    [Fact]
    public async Task TypedPreventiveBenefit_FlaggedAca_InNetwork_NoCostShare()
    {
        var stored = Plan(StandardCostSharing(), benefits: new PreventiveBenefit
        {
            ServiceCategory = "WELL",
            Description = "Annual Wellness",
            InNetworkCopay = 20m,
            IsAcaPreventive = true,
        });

        var line = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 200m, "WELL")).Lines.Single();
        line.MemberResponsibility.Should().Be(0m);
    }

    [Fact]
    public async Task TypedPreventiveBenefit_NotAca_KeepsNormalCostShare()
    {
        var stored = Plan(StandardCostSharing(), benefits: new PreventiveBenefit
        {
            ServiceCategory = "WELL",
            Description = "Grade C screening",
            IsAcaPreventive = false,
            UspstfRecommendationGrade = "C",
        });

        var line = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 200m, "WELL")).Lines.Single();
        line.DeductibleAmount.Should().Be(200m);
    }

    [Fact]
    public async Task Hdhp_PreventiveExempt_OtherServicesDeductibleFirst()
    {
        var stored = Plan(StandardCostSharing(), planType: ModelPlanType.HDHP,
            benefits:
            [
                new MedicalBenefit { ServiceCategory = "Preventive", Description = "Preventive Care" },
                OfficeVisitBenefit(deductibleApplies: false),
            ]);

        var preventive = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 200m, "Preventive")).Lines.Single();
        preventive.MemberResponsibility.Should().Be(0m);

        var visit = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 200m)).Lines.Single();
        visit.DeductibleAmount.Should().Be(200m);
    }

    // ── Coinsurance scale ──────────────────────────────────────────────

    [Fact]
    public async Task PercentScaleCoinsurance_NormalizedToFraction()
    {
        var stored = Plan(StandardCostSharing(), benefits: new MedicalBenefit
        {
            ServiceCategory = OfficeVisit,
            Description = "Office Visit",
            CoinsurancePercentage = 20m,   // legacy percent scale
            OutNetworkCoinsurance = 40m,   // percent scale, as the demo seed stores it
            DeductibleApplies = false,
        });

        var inn = (await AdjudicateAsync(stored, NetworkTier.InNetwork, 1_000m)).Lines.Single();
        inn.CoinsuranceAmount.Should().Be(200m);
        inn.CoinsurancePercent.Should().Be(0.20m);

        var oon = (await AdjudicateAsync(stored, NetworkTier.OutOfNetwork, 1_000m)).Lines.Single();
        oon.CoinsuranceAmount.Should().Be(400m);
    }

    private sealed class FixedCategoryResolver : IServiceCategoryResolver
    {
        private readonly string _code;
        public FixedCategoryResolver(string code) { _code = code; }

        public Task<ServiceCategoryMatch?> ResolveAsync(
            string tenantId, Guid benefitPlanId, DateOnly serviceDate,
            string procedureCode, string codeType, string placeOfService,
            IReadOnlyList<string> modifiers, string? revenueCode,
            CancellationToken ct = default)
            => Task.FromResult<ServiceCategoryMatch?>(new ServiceCategoryMatch
            {
                ServiceTypeCode = _code,
                ServiceTypeDescription = "Office Visit",
                MatchedBy = "Test",
                MatchedRule = "Test",
            });
    }

    /// <summary>
    /// No prior accumulator activity; the engine's working set seeds
    /// limits from the projected <see cref="BenefitPlanConfig"/>.
    /// </summary>
    private sealed class EmptyAccumulatorService : IAccumulatorService
    {
        public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
            string memberId, string subscriberId, Guid benefitPlanId,
            string planYear, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccumulatorSnapshot>>(Array.Empty<AccumulatorSnapshot>());

        public Task ApplyUpdatesAsync(string memberId, string subscriberId,
            Guid benefitPlanId, string planYear, string claimId,
            IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ReverseAsync(string memberId, string subscriberId,
            Guid benefitPlanId, string planYear, string claimId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubTenantContext : IBenefitEngineTenantContext
    {
        public StubTenantContext(string tenantId) { TenantId = tenantId; }
        public string TenantId { get; }
    }

    private sealed class StubLimits : IAcaLimitsProvider
    {
        private readonly Dictionary<int, AcaLimits> _byYear;
        public StubLimits(params AcaLimits[] rows) { _byYear = rows.ToDictionary(r => r.PlanYear); }
        public AcaLimits? GetForPlanYear(int planYear)
            => _byYear.TryGetValue(planYear, out var row) ? row : null;
        public IReadOnlyCollection<int> ConfiguredPlanYears => _byYear.Keys;
    }
}
