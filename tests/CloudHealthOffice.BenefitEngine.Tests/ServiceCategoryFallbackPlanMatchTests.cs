using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// End to end through <see cref="BenefitCalculationEngine"/> with the real
/// <see cref="ServiceCategoryResolver"/> and no mappings, so every line takes
/// the system-level fallback. The fallback used to emit X12 codes ("98",
/// "48"), which never matched a plan authored with named categories; it now
/// emits the names, and plans still keyed by X12 codes match through the
/// alias in <see cref="BenefitPlanConfig.GetCategories"/>.
/// </summary>
public class ServiceCategoryFallbackPlanMatchTests
{
    private static BenefitCategoryConfig Category(string code, decimal copay, decimal coinsurance) => new()
    {
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        IsCovered = true,
        InNetworkCostSharing =
        [
            new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = copay },
            new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = coinsurance },
        ],
    };

    private static BenefitPlanConfig Plan(params BenefitCategoryConfig[] categories) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        PlanName = "Fallback plan",
        PlanType = PlanType.PPO,
        PlanYear = "2026",
        IndividualDeductible = 0,
        FamilyDeductible = 0,
        IndividualOopMax = 10000,
        FamilyOopMax = 20000,
        Categories = [.. categories],
    };

    private static readonly BenefitPlanConfig NameKeyedPlan = Plan(
        Category(ServiceCategoryNames.OfficeVisit, copay: 30m, coinsurance: 0m),
        Category(ServiceCategoryNames.InpatientHospital, copay: 0m, coinsurance: 0.10m));

    private static async Task<LineBenefitResult> Adjudicate(
        BenefitPlanConfig plan, string procedureCode, string pos, string? claimType = "837P", string? typeOfBill = null)
    {
        var engine = new BenefitCalculationEngine(
            new ServiceCategoryResolver(new EmptyRepo(), NullLogger<ServiceCategoryResolver>.Instance),
            new InMemoryBenefitPlanProvider(plan),
            new InMemoryAccumulatorService(plan, 0, 0, 0, ServiceCategoryNames.OfficeVisit),
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        var result = await engine.CalculateAsync(new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = plan.Id,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = NetworkTier.InNetwork,
            ClaimType = claimType,
            TypeOfBill = typeOfBill,
            ClaimId = Guid.NewGuid().ToString(),
            Lines = [new ClaimLineInput { LineNumber = 1, ProcedureCode = procedureCode, PlaceOfService = pos, BilledAmount = 200m, Units = 1 }],
            AllowedAmounts = new Dictionary<int, decimal> { [1] = 100m },
        });

        return Assert.Single(result.Lines);
    }

    [Fact]
    public async Task ProfessionalPos11_OnNameKeyedPlan_PaysUnderOfficeVisitBenefit()
    {
        var line = await Adjudicate(NameKeyedPlan, "99213", "11");

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(ServiceCategoryNames.OfficeVisit, line.ServiceTypeCode);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(70m, line.PlanPaidAmount);
    }

    // Only POS 21 is inpatient. POS 22 (on-campus outpatient hospital) and
    // POS 23 (hospital emergency room) used to land here as well; see
    // ServiceCategoryResolverPlaceOfServiceTests for where they go now.
    [Fact]
    public async Task ProfessionalInpatientPos21_OnNameKeyedPlan_PaysUnderInpatientBenefit()
    {
        var line = await Adjudicate(NameKeyedPlan, "99223", "21");

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(ServiceCategoryNames.InpatientHospital, line.ServiceTypeCode);
        Assert.Equal(10m, line.CoinsuranceAmount);
        Assert.Equal(90m, line.PlanPaidAmount);
    }

    [Fact]
    public async Task ProfessionalPos11_OnX12KeyedPlan_StillMatchesThroughTheAlias()
    {
        var x12Plan = Plan(Category("98", copay: 30m, coinsurance: 0m));

        var line = await Adjudicate(x12Plan, "99213", "11");

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(30m, line.CopayAmount);
    }

    [Fact]
    public async Task InstitutionalInpatient_OnX12KeyedPlan_StillMatchesThroughTheAlias()
    {
        var x12Plan = Plan(Category("48", copay: 0m, coinsurance: 0.10m));

        // 837I with type of bill 111 (hospital inpatient).
        var line = await Adjudicate(x12Plan, "", "21", claimType: "837I", typeOfBill: "111");

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(10m, line.CoinsuranceAmount);
    }

    [Fact]
    public void GetCategories_PrefersTheExactName_OverTheX12Alias()
    {
        var plan = Plan(
            Category("98", copay: 99m, coinsurance: 0m),
            Category(ServiceCategoryNames.OfficeVisit, copay: 30m, coinsurance: 0m));

        var match = Assert.Single(plan.GetCategories(ServiceCategoryNames.OfficeVisit));
        Assert.Equal(ServiceCategoryNames.OfficeVisit, match.ServiceTypeCode);
    }

    [Fact]
    public void GetCategories_DoesNotAliasNamesTheFallbackNeverEmits()
    {
        var plan = Plan(Category("98", copay: 30m, coinsurance: 0m));

        Assert.Empty(plan.GetCategories("Specialist Visit"));
    }

    [Theory]
    [InlineData("98", ServiceCategoryNames.OfficeVisit)]
    [InlineData("48", ServiceCategoryNames.InpatientHospital)]
    [InlineData("50", ServiceCategoryNames.OutpatientHospital)]
    [InlineData("86", ServiceCategoryNames.EmergencyRoom)]
    [InlineData("UC", ServiceCategoryNames.UrgentCare)]
    [InlineData("13", ServiceCategoryNames.OutpatientSurgery)]
    [InlineData("AG", ServiceCategoryNames.SkilledNursing)]
    [InlineData("45", ServiceCategoryNames.Hospice)]
    [InlineData("A4", ServiceCategoryNames.BehavioralHealth)]
    [InlineData("5", ServiceCategoryNames.Laboratory)]
    [InlineData("PT", ServiceCategoryNames.PhysicalTherapy)]
    public void SharedMap_RoundTrips(string x12, string name)
    {
        Assert.Equal(name, ServiceCategoryNames.NameFor(x12));
        Assert.Equal(x12, ServiceCategoryNames.X12CodeFor(name));
    }

    private sealed class EmptyRepo : IServiceCategoryMappingRepository
    {
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServiceCategoryMapping>>(Array.Empty<ServiceCategoryMapping>());
    }
}
