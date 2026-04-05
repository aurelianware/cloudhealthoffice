using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MetricsServiceTests
{
    private readonly Mock<ILogger<MetricsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public MetricsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private MetricsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new MetricsService(httpClient, _configuration, _logger.Object);
    }

    // ── Service Unavailable Behavior ──

    [Fact]
    public async Task GetDashboardMetricsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetDashboardMetricsAsync());

        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetOperationalAlertsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetOperationalAlertsAsync());

        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetTodayEdiVolumeAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetTodayEdiVolumeAsync());

        ex.ServiceName.Should().Be("Claims Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests (API returns real data)
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetDashboardMetricsAsync_WhenApiReturns200_CalculatesMetricsFromSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalClaims = 1000, approvedClaims = 900, deniedClaims = 80, pendedClaims = 20,
            paidClaims = 850, totalChargeAmount = 500000m, totalAllowedAmount = 400000m,
            totalPaidAmount = 350000m, averageProcessingDays = 3.5m, approvalRate = 0.9m
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetDashboardMetricsAsync();

        result.TotalClaims.Should().Be(1000);
        result.ApprovedClaims.Should().Be(900);
        result.DeniedClaims.Should().Be(80);
        result.PendingClaims.Should().Be(20);
        result.ApprovalRate.Should().Be(0.9);
        result.TotalPayerAmount.Should().Be(350000m);
        handler.CapturedUrls[0].Should().Contain("/claims/summary");
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_WhenApiReturnsNull_ReturnsEmptyMetrics()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetDashboardMetricsAsync();

        result.TotalClaims.Should().Be(0);
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_ClaimsTrend_IsDecimalNotPercentage()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalClaims = 1000, approvedClaims = 900, deniedClaims = 80, pendedClaims = 20,
            paidClaims = 850, totalChargeAmount = 500000m, totalAllowedAmount = 400000m,
            totalPaidAmount = 350000m, averageProcessingDays = 3.5m, approvalRate = 0.9m
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetDashboardMetricsAsync();

        result.ClaimsTrend.Should().BeInRange(-1.0, 1.0,
            "ClaimsTrend should be a decimal fraction (e.g. 0.042), not a percentage (e.g. 4.2)");
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_ApprovalRate_IsDecimalNotPercentage()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalClaims = 1000, approvedClaims = 900, deniedClaims = 80, pendedClaims = 20,
            paidClaims = 850, totalChargeAmount = 500000m, totalAllowedAmount = 400000m,
            totalPaidAmount = 350000m, averageProcessingDays = 3.5m, approvalRate = 0.9m
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetDashboardMetricsAsync();

        result.ApprovalRate.Should().BeInRange(0, 1.0,
            "ApprovalRate should be a decimal fraction (e.g. 0.962), not a percentage (e.g. 96.2)");
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_ClaimCounts_AreConsistent()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalClaims = 1000, approvedClaims = 900, deniedClaims = 80, pendedClaims = 20,
            paidClaims = 850, totalChargeAmount = 500000m, totalAllowedAmount = 400000m,
            totalPaidAmount = 350000m, averageProcessingDays = 3.5m, approvalRate = 0.9m
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetDashboardMetricsAsync();

        var sum = result.ApprovedClaims + result.DeniedClaims + result.PendingClaims;
        result.TotalClaims.Should().Be(sum,
            "TotalClaims should equal Approved + Denied + Pending");
    }

    [Fact]
    public async Task GetOperationalAlertsAsync_WhenApiReturns200_DeserializesAlerts()
    {
        var json = JsonSerializer.Serialize(new
        {
            workQueueCount = 15, pendingRfais = 3, appealsDueThisWeek = 2, approachingFilingLimit = 1
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetOperationalAlertsAsync();

        result.WorkQueueCount.Should().Be(15);
        result.PendingRfais.Should().Be(3);
        result.AppealsDueThisWeek.Should().Be(2);
    }

    [Fact]
    public async Task GetTodayEdiVolumeAsync_WhenApiReturns200_DeserializesVolume()
    {
        var json = JsonSerializer.Serialize(new
        {
            claims837Received = 500, era835Generated = 400, eligibility270271 = 200, priorAuth278 = 50
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetTodayEdiVolumeAsync();

        result.Claims837Received.Should().Be(500);
        result.Era835Generated.Should().Be(400);
    }

    [Fact]
    public async Task GetTodayEdiVolumeAsync_UrlPointsToEdiVolumeEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetTodayEdiVolumeAsync();

        handler.CapturedUrls[0].Should().Contain("/metrics/edi-volume/today");
    }
}
