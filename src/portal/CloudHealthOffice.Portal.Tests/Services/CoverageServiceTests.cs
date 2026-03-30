using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CoverageServiceTests
{
    private readonly Mock<ILogger<CoverageService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CoverageServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://localhost:5009"
            })
            .Build();
    }

    private CoverageService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CoverageService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.ServiceName.Should().Be("Coverage Service");
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.Message.Should().Contain("Coverage Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetCoverageByMemberIdAsync_WhenApiReturns200_DeserializesCoverageList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { coverageId = "COV-1", planName = "Gold PPO", groupNumber = "GRP-100",
                  effectiveDate = "2024-01-01", terminationDate = (string?)null, status = "Active" },
            new { coverageId = "COV-2", planName = "Dental Basic", groupNumber = "GRP-100",
                  effectiveDate = "2024-01-01", terminationDate = (string?)"2024-12-31", status = "Terminated" }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCoverageByMemberIdAsync("MBR-001");

        result.Should().HaveCount(2);
        result[0].PlanName.Should().Be("Gold PPO");
        result[0].Status.Should().Be("Active");
        result[1].Status.Should().Be("Terminated");
        handler.CapturedUrls[0].Should().Contain("/v1/coverage/member/MBR-001");
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetCoverageByMemberIdAsync("MBR-NONE");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_WhenApiReturnsEmptyArray_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "[]")));
        var result = await sut.GetCoverageByMemberIdAsync("MBR-NONE");
        result.Should().BeEmpty();
    }
}
