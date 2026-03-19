using System.Net;
using Microsoft.Extensions.Configuration;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MetricsServiceTests
{
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
        return new MetricsService(httpClient, _configuration);
    }

    // ── Dashboard Metrics (existing, but verify decimal fix) ──

    [Fact]
    public async Task GetDashboardMetricsAsync_ClaimsTrend_IsDecimalNotPercentage()
    {
        var sut = CreateService();
        var result = await sut.GetDashboardMetricsAsync();

        // Bug fix: ClaimsTrend should be 0.042 (decimal), not 4.2 (percentage)
        // Dashboard multiplies by 100 for display
        result.ClaimsTrend.Should().BeInRange(-1.0, 1.0,
            "ClaimsTrend should be a decimal fraction (e.g. 0.042), not a percentage (e.g. 4.2)");
        (result.ClaimsTrend * 100).Should().BeApproximately(4.2, 0.01);
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_ApprovalRate_IsDecimalNotPercentage()
    {
        var sut = CreateService();
        var result = await sut.GetDashboardMetricsAsync();

        // Bug fix: ApprovalRate should be 0.962 (decimal), not 96.2 (percentage)
        result.ApprovalRate.Should().BeInRange(0, 1.0,
            "ApprovalRate should be a decimal fraction (e.g. 0.962), not a percentage (e.g. 96.2)");
        (result.ApprovalRate * 100).Should().BeApproximately(96.2, 0.01);
    }

    [Fact]
    public async Task GetDashboardMetricsAsync_ClaimCounts_AreConsistent()
    {
        var sut = CreateService();
        var result = await sut.GetDashboardMetricsAsync();

        var sum = result.ApprovedClaims + result.DeniedClaims + result.PendingClaims;
        result.TotalClaims.Should().Be(sum,
            "TotalClaims should equal Approved + Denied + Pending");
    }

    // ── Operational Alerts ──

    [Fact]
    public async Task GetOperationalAlertsAsync_WhenApiFails_ReturnsMockAlerts()
    {
        var sut = CreateService();
        var result = await sut.GetOperationalAlertsAsync();

        result.Should().NotBeNull();
        result.WorkQueueCount.Should().BeGreaterThan(0);
        result.PendingRfais.Should().BeGreaterThan(0);
        result.AppealsDueThisWeek.Should().BeGreaterThan(0);
        result.ApproachingFilingLimit.Should().BeGreaterOrEqualTo(0);
    }

    // ── EDI Volume ──

    [Fact]
    public async Task GetTodayEdiVolumeAsync_WhenApiFails_ReturnsMockVolume()
    {
        var sut = CreateService();
        var result = await sut.GetTodayEdiVolumeAsync();

        result.Should().NotBeNull();
        result.Claims837Received.Should().BeGreaterThan(0);
        result.Era835Generated.Should().BeGreaterThan(0);
        result.Eligibility270271.Should().BeGreaterThan(0);
        result.PriorAuth278.Should().BeGreaterThan(0);
    }
}
