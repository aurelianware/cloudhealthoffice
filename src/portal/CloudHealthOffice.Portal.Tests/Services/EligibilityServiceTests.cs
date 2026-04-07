using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EligibilityServiceTests
{
    private readonly Mock<ILogger<EligibilityService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EligibilityServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:EligibilityService"] = "http://localhost:5005"
            })
            .Build();
    }

    private EligibilityService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EligibilityService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task CheckEligibilityAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.ServiceName.Should().Be("Eligibility Service");
    }

    [Fact]
    public async Task CheckEligibilityAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task CheckEligibilityAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.Message.Should().Contain("Eligibility Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CheckEligibilityAsync_WhenApiReturns200_DeserializesResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            isCovered = true, insurancePlanName = "Gold PPO",
            groupNumber = "GRP-100", coverageLevel = "Employee+Family",
            coverageBeginDate = "2024-01-01",
            deductible = new { individualDeductible = 1500m, individualDeductibleMet = 750m,
                               familyDeductible = 3000m, familyDeductibleMet = 1200m },
            outOfPocket = new { individualOOPMax = 6000m, individualOOPMet = 2000m,
                                familyOOPMax = 12000m, familyOOPMet = 4000m },
            benefits = new[]
            {
                new { serviceTypeName = "Office Visit", monetaryAmount = 30m,
                      percentage = 0.8m, authorizationRequired = false }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.CheckEligibilityAsync(new { MemberId = "MBR-001", ServiceDate = "2025-03-15" });

        result.IsCovered.Should().BeTrue();
        result.InsurancePlanName.Should().Be("Gold PPO");
        result.CoverageLevel.Should().Be("Employee+Family");
        result.Deductible.Should().NotBeNull();
        result.Deductible!.IndividualAmount.Should().Be(1500m);
        result.Benefits.Should().HaveCount(1);
        result.Benefits![0].ServiceTypeName.Should().Be("Office Visit");
    }

    [Fact]
    public async Task CheckEligibilityAsync_PostsToCorrectUrl()
    {
        var json = JsonSerializer.Serialize(new { isCovered = false, insurancePlanName = "", groupNumber = "", coverageLevel = "" }, JsonOpts);
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        await sut.CheckEligibilityAsync(new { MemberId = "MBR-001" });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/eligibility/inquiry");
    }

    [Fact]
    public async Task CheckEligibilityAsync_WhenNotCovered_HasRejectionReason()
    {
        var json = JsonSerializer.Serialize(new
        {
            isCovered = false, rejectionReason = "Coverage terminated",
            insurancePlanName = "", groupNumber = "", coverageLevel = ""
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CheckEligibilityAsync(new { MemberId = "MBR-TERMED" });

        result.IsCovered.Should().BeFalse();
        result.RejectionReason.Should().Be("Coverage terminated");
    }
}
