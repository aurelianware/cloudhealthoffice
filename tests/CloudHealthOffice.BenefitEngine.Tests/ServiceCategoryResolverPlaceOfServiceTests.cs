using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// The professional place-of-service fallback
/// (<see cref="ServiceCategoryResolver.ProfessionalPlaceOfServiceMap"/>),
/// audited against the CMS Place of Service code set. Several entries were
/// wrong and gave real claims the wrong benefit: POS 81 (independent
/// laboratory) was dental, POS 22/23 (outpatient hospital / ER) were
/// inpatient, POS 31/32 (SNF / nursing facility) and 51–54 were emergency
/// room, POS 34 (hospice) was home health, POS 62 (outpatient rehab) was
/// inpatient, and POS 71/72 (public health / rural health clinic) were
/// psychiatric.
/// </summary>
public class ServiceCategoryResolverPlaceOfServiceTests
{
    private static Task<ServiceCategoryMatch?> Resolve(string pos, string procedureCode = "ZZZZZ") =>
        new ServiceCategoryResolver(new EmptyRepo(), NullLogger<ServiceCategoryResolver>.Instance)
            .ResolveAsync("tenant-a", Guid.NewGuid(), new DateOnly(2026, 3, 8),
                procedureCode, "CPT", pos, Array.Empty<string>(), revenueCode: null,
                new ServiceCategoryClaimContext("837P", null));

    private static async Task AssertResolvesTo(string pos, string expectedName)
    {
        var match = await Resolve(pos);

        Assert.NotNull(match);
        Assert.Equal(expectedName, match!.ServiceTypeCode);
        Assert.Equal("SystemDefault", match.MatchedBy);
        Assert.Equal($"POS-fallback:{pos}", match.MatchedRule);
    }

    // ---- One test per corrected POS ------------------------------------

    [Fact]
    public Task Pos81_IndependentLaboratory_IsLaboratory_NotDental()
        => AssertResolvesTo("81", ServiceCategoryNames.Laboratory);

    [Fact]
    public Task Pos22_OnCampusOutpatientHospital_IsOutpatientHospital_NotInpatient()
        => AssertResolvesTo("22", ServiceCategoryNames.OutpatientHospital);

    [Fact]
    public Task Pos23_EmergencyRoomHospital_IsEmergencyRoom_NotInpatient()
        => AssertResolvesTo("23", ServiceCategoryNames.EmergencyRoom);

    [Fact]
    public Task Pos20_UrgentCareFacility_IsUrgentCare_NotOutpatientHospital()
        => AssertResolvesTo("20", ServiceCategoryNames.UrgentCare);

    [Fact]
    public Task Pos24_AmbulatorySurgicalCenter_IsOutpatientSurgery_NotOutpatientHospital()
        => AssertResolvesTo("24", ServiceCategoryNames.OutpatientSurgery);

    [Fact]
    public Task Pos31_SkilledNursingFacility_IsSkilledNursing_NotEmergencyRoom()
        => AssertResolvesTo("31", ServiceCategoryNames.SkilledNursing);

    [Fact]
    public async Task Pos32_NursingFacility_IsUnmapped_NotSkilledNursing()
    {
        // A POS 32 nursing facility is mostly custodial / long-term care, not
        // a skilled stay; like 33, the procedure code decides (CARC 204 when
        // nothing maps it) rather than applying the SNF benefit.
        Assert.Null(await Resolve("32"));
    }

    [Fact]
    public Task Pos34_Hospice_IsHospice_NotHomeHealth()
        => AssertResolvesTo("34", ServiceCategoryNames.Hospice);

    [Fact]
    public Task Pos51_InpatientPsychiatricFacility_IsBehavioralHealth_NotEmergencyRoom()
        => AssertResolvesTo("51", ServiceCategoryNames.BehavioralHealth);

    [Fact]
    public Task Pos52_PsychiatricPartialHospitalization_IsBehavioralHealth_NotEmergencyRoom()
        => AssertResolvesTo("52", ServiceCategoryNames.BehavioralHealth);

    [Fact]
    public Task Pos53_CommunityMentalHealthCenter_IsBehavioralHealth_NotEmergencyRoom()
        => AssertResolvesTo("53", ServiceCategoryNames.BehavioralHealth);

    [Fact]
    public async Task Pos54_IntermediateCareFacilityIid_IsUnmapped_NotEmergencyRoom()
    {
        // ICF/IID is long-term care for people with intellectual
        // disabilities: neither psychiatric nor emergency, and no plan
        // category fits. Null → CARC 204 rather than a guessed benefit.
        Assert.Null(await Resolve("54"));
    }

    [Fact]
    public async Task Pos62_ComprehensiveOutpatientRehab_IsUnmapped_NotPhysicalTherapy()
    {
        // A CORF also bills respiratory therapy, social work and psych
        // services; the procedure code decides, not the setting.
        Assert.Null(await Resolve("62"));
    }

    [Fact]
    public Task Pos71_PublicHealthClinic_IsOfficeVisit_NotPsychiatric()
        => AssertResolvesTo("71", ServiceCategoryNames.OfficeVisit);

    [Fact]
    public Task Pos72_RuralHealthClinic_IsOfficeVisit_NotPsychiatric()
        => AssertResolvesTo("72", ServiceCategoryNames.OfficeVisit);

    // ---- Table-driven test over the whole CMS POS code set --------------

    /// <summary>
    /// Every CMS place of service (01–99) and what the professional fallback
    /// must do with it; null means "do not guess" (CARC 204 upstream).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> ExpectedByPos = new Dictionary<string, string?>
    {
        ["01"] = null, // Pharmacy: the drug / item decides
        ["02"] = null, // Telehealth other than home: the service decides
        ["03"] = null, // School
        ["04"] = null, // Homeless shelter
        ["05"] = null, // IHS free-standing
        ["06"] = null, // IHS provider-based
        ["07"] = null, // Tribal 638 free-standing
        ["08"] = null, // Tribal 638 provider-based
        ["09"] = null, // Prison / correctional facility
        ["10"] = null, // Telehealth in patient's home
        ["11"] = ServiceCategoryNames.OfficeVisit,
        ["12"] = null, // Home: home health vs house call depends on the procedure
        ["13"] = null, // Assisted living facility
        ["14"] = null, // Group home
        ["15"] = null, // Mobile unit
        ["16"] = null, // Temporary lodging
        ["17"] = null, // Walk-in retail health clinic
        ["18"] = null, // Place of employment / worksite
        ["19"] = ServiceCategoryNames.OutpatientHospital,
        ["20"] = ServiceCategoryNames.UrgentCare,
        ["21"] = ServiceCategoryNames.InpatientHospital,
        ["22"] = ServiceCategoryNames.OutpatientHospital,
        ["23"] = ServiceCategoryNames.EmergencyRoom,
        ["24"] = ServiceCategoryNames.OutpatientSurgery,
        ["25"] = null, // Birthing center: maternity is procedure-driven
        ["26"] = null, // Military treatment facility
        ["27"] = null, // Outreach site / street
        ["31"] = ServiceCategoryNames.SkilledNursing,
        ["32"] = null, // Nursing facility: mostly custodial; the procedure decides
        ["33"] = null, // Custodial care facility: no medical component
        ["34"] = ServiceCategoryNames.Hospice,
        ["41"] = null, // Ambulance - land: ambulance HCPCS decides
        ["42"] = null, // Ambulance - air or water
        ["49"] = ServiceCategoryNames.OfficeVisit,
        ["50"] = ServiceCategoryNames.OfficeVisit,
        ["51"] = ServiceCategoryNames.BehavioralHealth,
        ["52"] = ServiceCategoryNames.BehavioralHealth,
        ["53"] = ServiceCategoryNames.BehavioralHealth,
        ["54"] = null, // ICF/IID: long-term care, no plan category
        ["55"] = ServiceCategoryNames.BehavioralHealth,
        ["56"] = ServiceCategoryNames.BehavioralHealth,
        ["57"] = ServiceCategoryNames.BehavioralHealth,
        ["58"] = ServiceCategoryNames.BehavioralHealth,
        ["60"] = null, // Mass immunization center
        ["61"] = ServiceCategoryNames.InpatientHospital,
        ["62"] = null, // CORF: therapy, respiratory, social work, psych; the procedure decides
        ["65"] = null, // ESRD treatment facility
        ["66"] = null, // PACE center
        ["71"] = ServiceCategoryNames.OfficeVisit,
        ["72"] = ServiceCategoryNames.OfficeVisit,
        ["81"] = ServiceCategoryNames.Laboratory,
        ["99"] = null, // Other place of service
    };

    public static TheoryData<string> AllPlacesOfService()
    {
        var data = new TheoryData<string>();
        for (var i = 1; i <= 99; i++)
            data.Add(i.ToString("00"));
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPlacesOfService))]
    public async Task EveryPlaceOfService_ResolvesAsAudited(string pos)
    {
        // Unassigned POS codes (28–30, 35–40, 43–48, 59, 63–64, 67–70,
        // 73–80, 82–98) must never resolve.
        var expected = ExpectedByPos.TryGetValue(pos, out var name) ? name : null;

        var match = await Resolve(pos);

        Assert.Equal(expected, match?.ServiceTypeCode);
    }

    [Fact]
    public void Map_HasExactlyTheAuditedEntries()
    {
        var mapped = ExpectedByPos.Where(kv => kv.Value is not null).Select(kv => kv.Key).Order();

        Assert.Equal(mapped, ServiceCategoryResolver.ProfessionalPlaceOfServiceMap.Keys.Order());
    }

    [Fact]
    public void EveryMappedX12Code_HasACategoryName()
    {
        foreach (var (pos, x12) in ServiceCategoryResolver.ProfessionalPlaceOfServiceMap)
            Assert.True(ServiceCategoryNames.NameFor(x12) is not null, $"POS {pos} → X12 {x12} has no category name");
    }

    [Theory]
    [InlineData("A4")]
    [InlineData("MH")]
    [InlineData("AI")]
    public void BehavioralHealth_AliasesEveryX12Code(string x12)
    {
        Assert.Contains(x12, ServiceCategoryNames.X12CodesFor(ServiceCategoryNames.BehavioralHealth));
        Assert.Equal("A4", ServiceCategoryNames.X12CodeFor(ServiceCategoryNames.BehavioralHealth));
    }

    [Theory]
    [InlineData("A4")]
    [InlineData("MH")]
    [InlineData("AI")]
    public void GetCategories_BehavioralHealth_MatchesAPlanKeyedByAnyOfItsX12Codes(string x12)
    {
        var plan = Plan(Category(x12));

        var match = Assert.Single(plan.GetCategories(ServiceCategoryNames.BehavioralHealth));
        Assert.Equal(x12, match.ServiceTypeCode);
    }

    // ---- End to end: claims that change category, on name- and X12-keyed plans

    [Theory]
    [InlineData("81", "80053", ServiceCategoryNames.Laboratory)]
    [InlineData("22", "99213", ServiceCategoryNames.OutpatientHospital)]
    [InlineData("23", "99283", ServiceCategoryNames.EmergencyRoom)]
    [InlineData("31", "99309", ServiceCategoryNames.SkilledNursing)]
    [InlineData("71", "99213", ServiceCategoryNames.OfficeVisit)]
    public async Task Engine_NameKeyedPlan_PaysUnderTheCorrectedCategory(string pos, string cpt, string category)
    {
        var plan = Plan(
            Category(ServiceCategoryNames.InpatientHospital, copay: 500m),
            Category(ServiceCategoryNames.Dental, covered: false),
            Category(category, copay: 10m));

        var line = await Adjudicate(plan, cpt, pos);

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(category, line.ServiceTypeCode);
        Assert.Equal(10m, line.CopayAmount);
    }

    [Theory]
    [InlineData("81", "5")]
    [InlineData("22", "50")]
    [InlineData("23", "86")]
    [InlineData("31", "AG")]
    [InlineData("53", "MH")]
    [InlineData("72", "98")]
    public async Task Engine_X12KeyedPlan_StillMatchesThroughTheAlias(string pos, string x12)
    {
        var plan = Plan(Category("48", copay: 500m), Category(x12, copay: 10m));

        var line = await Adjudicate(plan, "99999", pos);

        Assert.True(line.IsCovered, line.DenialReasonDescription);
        Assert.Equal(10m, line.CopayAmount);
    }

    [Fact]
    public async Task Engine_Pos54_DeniesWithCarc204()
    {
        var plan = Plan(Category(ServiceCategoryNames.EmergencyRoom, copay: 10m));

        var line = await Adjudicate(plan, "99309", "54");

        Assert.False(line.IsCovered);
        Assert.Equal("204", line.DenialReasonCode);
    }

    private static BenefitCategoryConfig Category(string code, decimal copay = 0m, bool covered = true) => new()
    {
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        IsCovered = covered,
        InNetworkCostSharing = [new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = copay }],
    };

    private static BenefitPlanConfig Plan(params BenefitCategoryConfig[] categories) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        PlanName = "POS fallback plan",
        PlanType = PlanType.PPO,
        PlanYear = "2026",
        IndividualDeductible = 0,
        FamilyDeductible = 0,
        IndividualOopMax = 10000,
        FamilyOopMax = 20000,
        Categories = [.. categories],
    };

    private static async Task<LineBenefitResult> Adjudicate(BenefitPlanConfig plan, string procedureCode, string pos)
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
}
