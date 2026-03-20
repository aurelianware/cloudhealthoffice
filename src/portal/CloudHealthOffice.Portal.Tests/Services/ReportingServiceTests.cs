using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ReportingServiceTests
{
    private readonly Mock<ILogger<ReportingService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ReportingServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000",
                ["Services:PaymentService"] = "http://localhost:5006",
                ["Services:EligibilityService"] = "http://localhost:5005",
                ["Services:AuthorizationService"] = "http://localhost:5003"
            })
            .Build();
    }

    private ReportingService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ReportingService(httpClient, _configuration, _logger.Object);
    }

    // ── GetClaimsSummaryAsync ──

    [Fact]
    public async Task GetClaimsSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetClaimsSummaryAsync(new ReportRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetPaymentSummaryAsync ──

    [Fact]
    public async Task GetPaymentSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetPaymentSummaryAsync(new ReportRequest()));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── GetEligibilityStatsAsync ──

    [Fact]
    public async Task GetEligibilityStatsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetEligibilityStatsAsync(new ReportRequest()));
        ex.ServiceName.Should().Be("Eligibility Service");
    }

    // ── GetAuthApprovalReportAsync ──

    [Fact]
    public async Task GetAuthApprovalReportAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAuthApprovalReportAsync(new ReportRequest()));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    // ── GetProviderPerformanceAsync ──

    [Fact]
    public async Task GetProviderPerformanceAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetProviderPerformanceAsync(new ReportRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetPaymentSummaryAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetPaymentSummaryAsync(new ReportRequest()));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
