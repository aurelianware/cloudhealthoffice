using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// The system-level fallback for institutional claims. On an 837I the line's
/// place of service carries CLM05-1, the facility type code ("11" = hospital
/// inpatient), so reading it as a CMS place of service (11 = office) denied an
/// unmapped inpatient stay as an office visit. Institutional claims now infer
/// the category from the type of bill / facility type and the revenue code;
/// professional claims keep place-of-service inference.
/// </summary>
public class ServiceCategoryResolverInstitutionalFallbackTests
{
    private static readonly ServiceCategoryMapping InpatientRev = new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        ServiceTypeCode = "Inpatient Hospital",
        ServiceTypeDescription = "Inpatient Hospital",
        Rules = [new ProcedureCodeRule { Priority = 20, CodeType = "REV", CodePattern = "0100", CodeRangeEnd = "0219" }],
    };

    private static readonly ServiceCategoryClaimContext Inpatient837I = new("837I", "111");

    private static Task<ServiceCategoryMatch?> Resolve(
        ServiceCategoryClaimContext? claim,
        string? revenueCode,
        string placeOfService = "11",
        string procedureCode = "",
        params ServiceCategoryMapping[] mappings) =>
        new ServiceCategoryResolver(new Repo(mappings), NullLogger<ServiceCategoryResolver>.Instance)
            .ResolveAsync("tenant-a", Guid.NewGuid(), new DateOnly(2026, 2, 10),
                procedureCode, "CPT", placeOfService, Array.Empty<string>(), revenueCode, claim);

    [Fact]
    public async Task Institutional_WithTenantRevRule_UsesTheTenantMapping()
    {
        var match = await Resolve(Inpatient837I, revenueCode: "0120", mappings: InpatientRev);

        Assert.Equal("Inpatient Hospital", match!.ServiceTypeCode);
        Assert.Equal("TenantDefault", match.MatchedBy);
    }

    [Theory]
    [InlineData("0120")] // room and board
    [InlineData("0250")] // pharmacy
    [InlineData("0450")] // ER charges on an inpatient bill belong to the stay
    [InlineData(null)]
    public async Task InpatientBill_WithoutTenantRevRule_IsHospitalInpatientNotOffice(string? revenueCode)
    {
        var match = await Resolve(Inpatient837I, revenueCode);

        Assert.Equal(ServiceCategoryResolver.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("SystemDefault", match.MatchedBy);
        Assert.Equal("TOB-fallback:11", match.MatchedRule);
    }

    [Theory]
    [InlineData("0111")] // four-digit NUBC form with the leading zero
    [InlineData("117")]  // replacement of a prior claim
    public async Task InpatientBill_FacilityTypeIsReadFromAnyTobForm(string typeOfBill)
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", typeOfBill), revenueCode: "0250");

        Assert.Equal(ServiceCategoryResolver.InpatientHospital, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task Institutional_WithoutTypeOfBill_ReadsFacilityTypeFromClm05Slot()
    {
        // ClaimType 837I but no composed TOB: the POS slot still carries CLM05-1.
        var match = await Resolve(new ServiceCategoryClaimContext("837I", null), revenueCode: "0250", placeOfService: "11");

        Assert.Equal(ServiceCategoryResolver.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("FacilityType-fallback:11", match.MatchedRule);
    }

    [Fact]
    public async Task TypeOfBillAlone_MarksTheClaimInstitutional()
    {
        var match = await Resolve(new ServiceCategoryClaimContext(null, "111"), revenueCode: "0250");

        Assert.Equal(ServiceCategoryResolver.InpatientHospital, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task OutpatientBill_IsHospitalOutpatient()
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "131"), revenueCode: "0300", placeOfService: "13");

        Assert.Equal(ServiceCategoryResolver.OutpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("TOB-fallback:13", match.MatchedRule);
    }

    [Theory]
    [InlineData("0450")]
    [InlineData("0459")]
    [InlineData("450")]
    public async Task ErRevenueCode_OnOutpatientBill_IsEmergency(string revenueCode)
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "131"), revenueCode, placeOfService: "13");

        Assert.Equal(ServiceCategoryResolver.EmergencyRoom, match!.ServiceTypeCode);
        Assert.StartsWith("REV-fallback:045", match.MatchedRule);
    }

    [Fact]
    public async Task ErRevenueCode_WithUnrecognizedFacilityType_IsEmergency()
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "831"), revenueCode: "0450", placeOfService: "83");

        Assert.Equal(ServiceCategoryResolver.EmergencyRoom, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task Institutional_Unrecognized_ReturnsNullRatherThanAProfessionalGuess()
    {
        // Facility type 83 (ASC) with a non-specific revenue code: no
        // institutional inference, and no POS fallback (POS 83 is not a
        // CMS place of service anyway; facility type 11 would have been office).
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "831"), revenueCode: "0360", placeOfService: "83");

        Assert.Null(match);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("837P")]
    public async Task ProfessionalPos11_StillResolvesToOffice(string? claimType)
    {
        var claim = claimType is null ? null : new ServiceCategoryClaimContext(claimType, null);

        var match = await Resolve(claim, revenueCode: null, placeOfService: "11", procedureCode: "99213");

        Assert.Equal("98", match!.ServiceTypeCode);
        Assert.Equal("POS-fallback:11", match.MatchedRule);
    }

    private sealed class Repo : IServiceCategoryMappingRepository
    {
        private readonly IReadOnlyList<ServiceCategoryMapping> _mappings;
        public Repo(ServiceCategoryMapping[] mappings) => _mappings = mappings;
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult(benefitPlanId is null ? _mappings : Array.Empty<ServiceCategoryMapping>());
    }
}
