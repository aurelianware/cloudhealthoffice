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

    // As claims-service sends an 837I: TOB composed, and the POS slot holds CLM05-1.
    private static readonly ServiceCategoryClaimContext Inpatient837I = new("837I", "111", PlaceOfServiceIsFacilityType: true);

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

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("SystemDefault", match.MatchedBy);
        Assert.Equal("TOB-fallback:11", match.MatchedRule);
    }

    [Theory]
    [InlineData("0111")] // four-digit NUBC form with the leading zero
    [InlineData("117")]  // replacement of a prior claim
    public async Task InpatientBill_FacilityTypeIsReadFromAnyTobForm(string typeOfBill)
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", typeOfBill), revenueCode: "0250");

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task Institutional_WithoutTypeOfBill_ReadsFacilityTypeFromClm05Slot()
    {
        // claims-service 837I without a composed TOB: the POS slot carries CLM05-1.
        var match = await Resolve(new ServiceCategoryClaimContext("837I", null, PlaceOfServiceIsFacilityType: true), revenueCode: "0250", placeOfService: "11");

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("FacilityType-fallback:11", match.MatchedRule);
    }

    [Fact]
    public async Task TypeOfBillAlone_MarksTheClaimInstitutional()
    {
        var match = await Resolve(new ServiceCategoryClaimContext(null, "111"), revenueCode: "0250");

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task OutpatientBill_IsHospitalOutpatient()
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "131"), revenueCode: "0300", placeOfService: "13");

        Assert.Equal(ServiceCategoryNames.OutpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("TOB-fallback:13", match.MatchedRule);
    }

    [Theory]
    [InlineData("0450")]
    [InlineData("0459")]
    [InlineData("450")]
    public async Task ErRevenueCode_OnOutpatientBill_IsEmergency(string revenueCode)
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "131"), revenueCode, placeOfService: "13");

        Assert.Equal(ServiceCategoryNames.EmergencyRoom, match!.ServiceTypeCode);
        Assert.StartsWith("REV-fallback:045", match.MatchedRule);
    }

    [Fact]
    public async Task ErRevenueCode_WithUnrecognizedFacilityType_IsEmergency()
    {
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "831"), revenueCode: "0450", placeOfService: "83");

        Assert.Equal(ServiceCategoryNames.EmergencyRoom, match!.ServiceTypeCode);
    }

    [Fact]
    public async Task Institutional_Unrecognized_ReturnsNullRatherThanAProfessionalGuess()
    {
        // Facility type 83 (ASC) with a non-specific revenue code: no
        // institutional inference. The POS slot holds CLM05-1 ("11" here),
        // so there is no POS fallback either: read as POS 11 it would be office.
        var match = await Resolve(
            new ServiceCategoryClaimContext("837I", "831", PlaceOfServiceIsFacilityType: true),
            revenueCode: "0360", placeOfService: "11");

        Assert.Null(match);
    }

    [Fact]
    public async Task Clm05FacilityType21_WithoutTob_IsSkilledNursing()
    {
        // claims-service 837I: CLM05-1 = 21 (skilled nursing facility inpatient).
        var match = await Resolve(
            new ServiceCategoryClaimContext("837I", null, PlaceOfServiceIsFacilityType: true),
            revenueCode: "0250", placeOfService: "21");

        Assert.Equal(ServiceCategoryNames.SkilledNursing, match!.ServiceTypeCode);
        Assert.Equal("FacilityType-fallback:21", match.MatchedRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("837P")]
    [InlineData("837I")] // sync API: institutional, but PlaceOfService is a CMS POS
    public async Task CmsPos21_IsReadAsPlaceOfService_NotAsFacilityType(string? claimType)
    {
        var claim = claimType is null ? null : new ServiceCategoryClaimContext(claimType, null);

        var match = await Resolve(claim, revenueCode: "0250", placeOfService: "21", procedureCode: "99223");

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("POS-fallback:21", match.MatchedRule);
    }

    [Fact]
    public async Task SyncApi_BillType111_WithCmsPos21_IsInpatientHospital()
    {
        // MakeDrgStayRequest shape: BillType "111" + POS "21". The TOB decides;
        // POS 21 is never read as facility type 21 (skilled nursing).
        var match = await Resolve(new ServiceCategoryClaimContext("837I", "111"), revenueCode: "0250", placeOfService: "21");

        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("TOB-fallback:11", match.MatchedRule);
    }

    [Theory]
    [InlineData("111", "111")]
    [InlineData("0111", "111")]
    [InlineData(" 131 ", "131")]
    [InlineData("11", null)]
    [InlineData("111garbage", null)]
    [InlineData("1111", null)] // four digits without the leading zero
    [InlineData("01a1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TypeOfBill_IsThreeDigitsOrFourWithLeadingZero(string? typeOfBill, string? expected)
    {
        Assert.Equal(expected, ServiceCategoryClaimContext.NormalizeTypeOfBill(typeOfBill));
    }

    [Theory]
    [InlineData("11")]
    [InlineData("111garbage")]
    [InlineData("1111")]
    public async Task MalformedTypeOfBill_CountsAsNoTypeOfBill(string typeOfBill)
    {
        // No claim type and a malformed TOB: not institutional, so POS 11 is
        // an office visit, as for any professional claim.
        var match = await Resolve(new ServiceCategoryClaimContext(null, typeOfBill), revenueCode: null, placeOfService: "11", procedureCode: "99213");

        Assert.Equal(ServiceCategoryNames.OfficeVisit, match!.ServiceTypeCode);
        Assert.Equal("POS-fallback:11", match.MatchedRule);
    }

    [Fact]
    public async Task MalformedTypeOfBill_On837I_FallsBackToTheClm05FacilityType()
    {
        var match = await Resolve(
            new ServiceCategoryClaimContext("837I", "1111", PlaceOfServiceIsFacilityType: true),
            revenueCode: "0250", placeOfService: "13");

        Assert.Equal(ServiceCategoryNames.OutpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("FacilityType-fallback:13", match.MatchedRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("837P")]
    public async Task ProfessionalPos11_StillResolvesToOffice(string? claimType)
    {
        var claim = claimType is null ? null : new ServiceCategoryClaimContext(claimType, null);

        var match = await Resolve(claim, revenueCode: null, placeOfService: "11", procedureCode: "99213");

        Assert.Equal(ServiceCategoryNames.OfficeVisit, match!.ServiceTypeCode);
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
