using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class OperatingModeServiceTests
{
    private readonly Mock<ILogger<OperatingModeService>> _logger = new();
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public OperatingModeServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TenantService"] = "http://localhost:5020"
            })
            .Build();
    }

    private OperatingModeService CreateService(HttpClient httpClient)
        => new(httpClient, _configuration, _logger.Object);

    // ── GetOperatingModeAsync ──

    [Fact]
    public async Task GetOperatingModeAsync_WhenApiReturns200_DeserializesConfiguration()
    {
        var json = JsonSerializer.Serialize(new
        {
            tenantId = "tenant-1",
            engines = new Dictionary<string, string>
            {
                { "benefitCalculation", "augment" },
                { "rateResolution", "replace" }
            },
            updatedAt = "2025-03-01T10:00:00Z"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetOperatingModeAsync("tenant-1");

        result.TenantId.Should().Be("tenant-1");
        result.Engines["benefitCalculation"].Should().Be("augment");
        // NormalizeConfiguration merges defaults for missing engine keys
        result.Engines.Should().ContainKey("claimsAdjudication");
    }

    [Fact]
    public async Task GetOperatingModeAsync_UrlContainsTenantId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetOperatingModeAsync("tenant-42");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("/v1/tenants/tenant-42/operating-mode");
    }

    [Fact]
    public async Task GetOperatingModeAsync_WhenApiReturnsNull_ReturnsDefaultConfiguration()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetOperatingModeAsync("tenant-99");

        result.TenantId.Should().Be("tenant-99");
        result.Engines.Should().HaveCount(5);
        result.Engines["benefitCalculation"].Should().Be("replace");
        result.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetOperatingModeAsync_WhenApiFails_ReturnsDefaultConfiguration()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        var result = await sut.GetOperatingModeAsync("tenant-fail");

        result.TenantId.Should().Be("tenant-fail");
        result.Engines.Should().ContainKey("ncciEdits");
    }
}
