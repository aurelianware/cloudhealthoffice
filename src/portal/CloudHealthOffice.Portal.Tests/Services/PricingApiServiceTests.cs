using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class PricingApiServiceTests
{
    private readonly Mock<ILogger<PricingApiService>> _logger = new();
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public PricingApiServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:PricingApi"] = "http://localhost:5040",
                ["PricingApi:AdminSecret"] = "test-secret-123"
            })
            .Build();
    }

    private PricingApiService CreateService(HttpClient httpClient)
        => new(httpClient, _configuration, _logger.Object);

    // ── GetApiKeysAsync ──

    [Fact]
    public async Task GetApiKeysAsync_WhenApiReturns200_DeserializesApiKeyList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { apiKey = "key-abc", tenantName = "Acme Health", contactEmail = "admin@acme.com",
                  tier = "enterprise", monthlyLimit = 10000, currentMonthUsage = 2500,
                  createdAt = "2025-01-01T00:00:00Z", isActive = true }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetApiKeysAsync();

        result.Should().HaveCount(1);
        result[0].ApiKey.Should().Be("key-abc");
        result[0].TenantName.Should().Be("Acme Health");
        result[0].MonthlyLimit.Should().Be(10000);
        result[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetApiKeysAsync_SendsAdminSecretHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetApiKeysAsync();

        handler.CapturedRequests[0].Headers
            .GetValues("X-Admin-Secret").Should().ContainSingle()
            .Which.Should().Be("test-secret-123");
    }

    [Fact]
    public async Task GetApiKeysAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetApiKeysAsync());
        ex.ServiceName.Should().Be("Pricing API");
    }

    // ── CreateApiKeyAsync ──

    [Fact]
    public async Task CreateApiKeyAsync_WhenApiReturns200_DeserializesNewKey()
    {
        var json = JsonSerializer.Serialize(new
        {
            apiKey = "key-new", tenantName = "Beta Corp", contactEmail = "admin@beta.com",
            tier = "professional", monthlyLimit = 5000, currentMonthUsage = 0,
            createdAt = "2025-03-01T00:00:00Z", isActive = true
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.CreateApiKeyAsync("Beta Corp", "admin@beta.com", "professional");

        result.ApiKey.Should().Be("key-new");
        result.Tier.Should().Be("professional");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/api/v1/admin/api-keys");
    }

    [Fact]
    public async Task CreateApiKeyAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateApiKeyAsync("Test", "t@t.com", "starter"));
    }

    // ── DeactivateApiKeyAsync ──

    [Fact]
    public async Task DeactivateApiKeyAsync_WhenApiReturns200_SendsDeleteToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.DeactivateApiKeyAsync("key-abc");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Delete);
        handler.CapturedUrls[0].Should().Contain("/api/v1/admin/api-keys/key-abc");
    }

    [Fact]
    public async Task DeactivateApiKeyAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.DeactivateApiKeyAsync("key-x"));
    }

    // ── ResetUsageAsync ──

    [Fact]
    public async Task ResetUsageAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.ResetUsageAsync();

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/api/v1/admin/api-keys/reset-usage");
    }

    [Fact]
    public async Task ResetUsageAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.ResetUsageAsync());
    }

    // ── GetFeeSchedulesAsync ──

    [Fact]
    public async Task GetFeeSchedulesAsync_WhenApiReturns200_DeserializesList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "FS-1", name = "Medicare MPFS 2025", type = "mpfs", version = "2025",
                  codeCount = 15000, description = "Medicare Physician Fee Schedule",
                  lastUpdated = "2025-01-15T00:00:00Z" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetFeeSchedulesAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Medicare MPFS 2025");
        result[0].CodeCount.Should().Be(15000);
    }

    [Fact]
    public async Task GetFeeSchedulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetFeeSchedulesAsync());
    }

    // ── UploadFeeScheduleAsync ──

    [Fact]
    public async Task UploadFeeScheduleAsync_WhenApiReturns200_ReturnsUploadResult()
    {
        var json = JsonSerializer.Serialize(new { message = "Uploaded", codeCount = 500, feeScheduleId = "FS-NEW" }, JsonOpts);
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes("code,rate\n99213,150.00"));
        var result = await sut.UploadFeeScheduleAsync("mpfs", 2025, csvStream, "rates.csv", 100.0m);

        result.FeeScheduleId.Should().Be("FS-NEW");
        result.CodeCount.Should().Be(500);
        handler.CapturedUrls[0].Should().Contain("/fee-schedules/upload/mpfs");
        handler.CapturedUrls[0].Should().Contain("year=2025");
        handler.CapturedUrls[0].Should().Contain("baseRate=100.0");
    }

    [Fact]
    public async Task UploadFeeScheduleAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes("code,rate"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UploadFeeScheduleAsync("mpfs", 2025, csvStream, "rates.csv"));
    }

    // ── SeedDemoDataAsync ──

    [Fact]
    public async Task SeedDemoDataAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.SeedDemoDataAsync();

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/fee-schedules/seed-demo");
    }

    [Fact]
    public async Task SeedDemoDataAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SeedDemoDataAsync());
    }
}
