using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// The optional <c>claimType</c> and <c>billType</c> on a repricing request decide the
/// claim's setting the way adjudication does: an institutional claim, or one with a valid
/// NUBC type of bill, takes the facility rate; a professional or dental claim is priced by
/// place of service; a request with neither keeps the previous (professional) behaviour.
/// </summary>
public class RepricingClaimTypeTests
{
    private const string Rbrvs = "MEDICARE_RBRVS_2025";
    private const string Drg = "MEDICARE_DRG_2025";
    private const decimal NonFacility = 110.00m;
    private const decimal Facility = 75.00m;

    private readonly Mock<IFeeScheduleRepository> _repo = new();
    private readonly RepricingService _sut;

    public RepricingClaimTypeTests()
    {
        SetupSchedules(_repo);
        _sut = new RepricingService(_repo.Object, NullLogger<RepricingService>.Instance);
    }

    // ── Service: setting by claim type / type of bill ─────────────────

    [Fact]
    public async Task NoClaimTypeNoBillType_PricedAsProfessional_ByPlaceOfService()
    {
        var result = await _sut.RepriceClaimAsync(Request(claimType: null, billType: null, pos: "11"));

        result.ClaimType.Should().Be(ClaimType.Professional);
        result.BillType.Should().BeNull();
        result.Lines[0].AllowedAmount.Should().Be(NonFacility);
        result.Lines[0].Breakdown.FacilityIndicator.Should().Be("Non-Facility");
    }

    [Theory]
    [InlineData("11", 110.00)]
    [InlineData("22", 75.00)]
    public async Task Professional_PricedByPlaceOfService(string pos, double expected)
    {
        var result = await _sut.RepriceClaimAsync(Request(ClaimType.Professional, billType: null, pos));

        result.Lines[0].AllowedAmount.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("11", 110.00)]
    [InlineData("22", 75.00)]
    public async Task Dental_PricedByPlaceOfService(string pos, double expected)
    {
        var result = await _sut.RepriceClaimAsync(Request(ClaimType.Dental, billType: null, pos));

        result.ClaimType.Should().Be(ClaimType.Dental);
        result.Lines[0].AllowedAmount.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(ClaimType.Institutional)]
    [InlineData(ClaimType.Outpatient)]
    public async Task InstitutionalClaimTypes_TakeFacilityRate_EvenAtNonFacilityPos(ClaimType claimType)
    {
        // POS 11 (or the 837I facility type code) is not read for an institutional claim.
        var result = await _sut.RepriceClaimAsync(Request(claimType, billType: null, pos: "11"));

        result.ClaimType.Should().Be(claimType);
        result.Lines[0].AllowedAmount.Should().Be(Facility);
        result.Lines[0].Breakdown.FacilityIndicator.Should().Be("Facility");
    }

    [Theory]
    [InlineData("131", "131")]
    [InlineData("0131", "131")]
    [InlineData(" 111 ", "111")]
    public async Task ValidBillType_NoClaimType_InferredInstitutional_FacilityRate(string billType, string normalized)
    {
        var result = await _sut.RepriceClaimAsync(Request(claimType: null, billType, pos: "13"));

        result.ClaimType.Should().Be(ClaimType.Institutional);
        result.BillType.Should().Be(normalized);
        result.Lines[0].AllowedAmount.Should().Be(Facility);
    }

    [Fact]
    public async Task InstitutionalWithBillType_FacilityRate()
    {
        var result = await _sut.RepriceClaimAsync(Request(ClaimType.Institutional, "131", pos: "13"));

        result.Lines[0].AllowedAmount.Should().Be(Facility);
        result.BillType.Should().Be("131");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("N/A")]
    [InlineData("13")]
    [InlineData("1311")]
    [InlineData("13A")]
    public async Task InvalidBillType_Rejected(string billType)
    {
        var act = () => _sut.RepriceClaimAsync(Request(ClaimType.Institutional, billType, pos: "13"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*billType*");
        RepricingClaimSetting.Validate(Request(ClaimType.Institutional, billType, "13"))!.Code.Should().Be("INVALID_BILL_TYPE");
    }

    [Theory]
    [InlineData(ClaimType.Professional)]
    [InlineData(ClaimType.Dental)]
    public void BillTypeOnProfessionalOrDentalClaim_Rejected(ClaimType claimType)
    {
        var error = RepricingClaimSetting.Validate(Request(claimType, "131", "11"));

        error.Should().NotBeNull();
        error!.Code.Should().Be("INVALID_BILL_TYPE");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankBillType_IsAbsent(string? billType)
        => RepricingClaimSetting.Validate(Request(ClaimType.Professional, billType, "11")).Should().BeNull();

    [Theory]
    [InlineData(null, null, false, null)]
    [InlineData(ClaimType.Professional, null, false, null)]
    [InlineData(ClaimType.Dental, null, false, null)]
    [InlineData(ClaimType.Institutional, null, true, null)]
    [InlineData(ClaimType.Outpatient, "0131", true, "131")]
    [InlineData(ClaimType.Inpatient, "111", true, "111")]
    [InlineData(null, "131", true, "131")]
    public void BuildEngineRequests_CarriesInstitutionalAndBillType(
        ClaimType? claimType, string? billType, bool institutional, string? engineBillType)
    {
        var lines = RepricingService.BuildEngineRequests(Request(claimType, billType, "11"));

        lines.Should().OnlyContain(l => l.IsInstitutional == institutional && l.BillType == engineBillType);
    }

    [Fact]
    public async Task InstitutionalAgainstDrgSchedule_PricedByDrg()
    {
        var result = await _sut.RepriceClaimAsync(new RepricingRequest
        {
            FeeScheduleId = Drg,
            ClaimType = ClaimType.Institutional,
            BillType = "111",
            DrgCode = "470",
            Lines = [new ClaimLineRequest { ProcedureCode = "27447", Units = 1, BilledAmount = 40_000m }],
        });

        result.TotalAllowed.Should().Be(13_300.00m); // 1.9 × 7,000
        result.Lines[0].Breakdown.DrgRelativeWeight.Should().Be(1.9m);
    }

    // ── HTTP: JSON contract and 400s ──────────────────────────────────

    [Fact]
    public async Task Http_ClaimTypeAndBillType_AcceptedAndEchoed()
    {
        using var factory = Factory();
        var response = await Service(factory).PostAsJsonAsync("/api/v1/reprice", new
        {
            feeScheduleId = Rbrvs,
            claimType = "institutional",
            billType = "0131",
            placeOfService = "13",
            lines = new[] { new { procedureCode = "99213", units = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("claimType").GetString().Should().Be("institutional");
        data.GetProperty("billType").GetString().Should().Be("131");
        data.GetProperty("totalAllowed").GetDecimal().Should().Be(Facility);
    }

    [Fact]
    public async Task Http_NoClaimType_StillPricesAsProfessional()
    {
        using var factory = Factory();
        var response = await Service(factory).PostAsJsonAsync("/api/v1/reprice", new
        {
            feeScheduleId = Rbrvs,
            placeOfService = "11",
            lines = new[] { new { procedureCode = "99213", units = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("claimType").GetString().Should().Be("professional");
        data.TryGetProperty("billType", out _).Should().BeFalse();
        data.GetProperty("totalAllowed").GetDecimal().Should().Be(NonFacility);
    }

    [Theory]
    [InlineData("institutional", "N/A")]
    [InlineData("institutional", "12")]
    [InlineData("professional", "131")]
    public async Task Http_InvalidBillType_Is400(string claimType, string billType)
    {
        using var factory = Factory();
        var response = await Service(factory).PostAsJsonAsync("/api/v1/reprice", new
        {
            feeScheduleId = Rbrvs,
            claimType,
            billType,
            lines = new[] { new { procedureCode = "99213", units = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("INVALID_BILL_TYPE");
        factory.FeeScheduleRepository.Verify(r => r.GetScheduleInfoAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Http_Batch_InvalidBillType_Is400_NamesTheClaim()
    {
        using var factory = Factory();
        var line = new[] { new { procedureCode = "99213", units = 1 } };
        var response = await Service(factory).PostAsJsonAsync("/api/v1/reprice/batch", new object[]
        {
            new { feeScheduleId = Rbrvs, claimType = "professional", lines = line },
            new { feeScheduleId = Rbrvs, claimType = "institutional", billType = "0", lines = line },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("INVALID_BILL_TYPE");
        error.GetProperty("message").GetString().Should().StartWith("Claim 1:");
    }

    [Fact]
    public async Task OpenApi_DocumentsClaimTypeAndBillType()
    {
        using var factory = Factory();
        var json = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        var schemas = JsonDocument.Parse(json).RootElement.GetProperty("components").GetProperty("schemas");

        var request = schemas.GetProperty("RepricingRequest").GetProperty("properties");
        request.GetProperty("billType").GetProperty("description").GetString().Should().Contain("NUBC type of bill");
        request.TryGetProperty("claimType", out _).Should().BeTrue();
        schemas.GetProperty("RepricingRequest").TryGetProperty("required", out var required).Should().BeTrue();
        required.EnumerateArray().Select(e => e.GetString()).Should().NotContain("claimType");
        schemas.GetProperty("ClaimType").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(["professional", "institutional", "dental"]);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static RepricingRequest Request(ClaimType? claimType, string? billType, string pos) => new()
    {
        FeeScheduleId = Rbrvs,
        ClaimType = claimType,
        BillType = billType,
        PlaceOfService = pos,
        Lines = [new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1 }],
    };

    private static void SetupSchedules(Mock<IFeeScheduleRepository> repo)
    {
        repo.Setup(r => r.GetScheduleInfoAsync(Rbrvs)).ReturnsAsync(Info(Rbrvs, FeeScheduleType.MedicareRbrvs));
        repo.Setup(r => r.GetScheduleInfoAsync(Drg)).ReturnsAsync(Info(Drg, FeeScheduleType.MedicareDrg));
        repo.Setup(r => r.LookupCodesAsync(Rbrvs, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
            .ReturnsAsync([new FeeScheduleEntry
            {
                FeeScheduleId = Rbrvs, ProcedureCode = "99213", NonFacilityRate = NonFacility, FacilityRate = Facility,
            }]);
        repo.Setup(r => r.LookupDrgAsync(Drg, "470"))
            .ReturnsAsync(new FeeScheduleEntry { FeeScheduleId = Drg, ProcedureCode = "470", DrgWeight = 1.9m, DrgBaseRate = 7_000m });
    }

    private static FeeScheduleInfo Info(string id, FeeScheduleType type) => new()
    {
        Id = id, Name = id, Type = type, Version = "2025.1",
        EffectiveDate = new DateOnly(2025, 1, 1), CodeCount = 1, LastUpdated = DateTimeOffset.UtcNow,
    };

    private static PricingApiFactory Factory()
    {
        var factory = new PricingApiFactory();
        SetupSchedules(factory.FeeScheduleRepository);
        return factory;
    }

    private static HttpClient Service(PricingApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", "tenant-a"));
        return client;
    }
}
