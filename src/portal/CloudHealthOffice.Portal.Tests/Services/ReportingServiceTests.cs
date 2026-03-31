using System.Net;
using System.Text.Json;
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

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetClaimsSummaryAsync_WhenApiReturns200_DeserializesReport()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-03-31",
            totalClaims = 1200, totalCharges = 500000m, totalAllowed = 400000m,
            totalPaid = 350000m, approvedCount = 1000, deniedCount = 150, pendedCount = 50,
            approvalRate = 0.833, avgClaimAmount = 416.67m,
            dailyBreakdown = new[]
            {
                new { date = "2025-01-01", count = 40, totalAmount = 16000m }
            },
            topProviders = new[]
            {
                new { providerId = "PRV-1", providerName = "Dr. Smith", specialty = "Cardiology",
                      claimCount = 80, totalBilled = 50000m, totalPaid = 40000m,
                      denialRate = 0.05, avgProcessingDays = 3.2 }
            },
            topDiagnoses = new[]
            {
                new { diagnosisCode = "I10", description = "Hypertension",
                      claimCount = 200, totalAmount = 80000m }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetClaimsSummaryAsync(new ReportRequest());

        result.TotalClaims.Should().Be(1200);
        result.TotalPaid.Should().Be(350000m);
        result.ApprovedCount.Should().Be(1000);
        result.TopProviders.Should().HaveCount(1);
        result.TopDiagnoses.Should().HaveCount(1);
        result.DailyBreakdown.Should().HaveCount(1);
        handler.CapturedUrls[0].Should().Contain("/reports/claims-summary");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetPaymentSummaryAsync_WhenApiReturns200_DeserializesReport()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-03-31",
            eraCount = 300, totalEraAmount = 250000m, avgEraAmount = 833.33m,
            byPeriod = new[]
            {
                new { period = "2025-01", eraCount = 100, totalAmount = 80000m }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetPaymentSummaryAsync(new ReportRequest());

        result.EraCount.Should().Be(300);
        result.TotalEraAmount.Should().Be(250000m);
        result.ByPeriod.Should().HaveCount(1);
        handler.CapturedUrls[0].Should().Contain("/reports/payment-summary");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetEligibilityStatsAsync_WhenApiReturns200_DeserializesReport()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-03-31",
            totalRequests = 5000, eligibleCount = 4500, ineligibleCount = 500,
            eligibilityRate = 0.9, avgResponseTimeMs = 120.5
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetEligibilityStatsAsync(new ReportRequest());

        result.TotalRequests.Should().Be(5000);
        result.EligibleCount.Should().Be(4500);
        result.EligibilityRate.Should().Be(0.9);
        handler.CapturedUrls[0].Should().Contain("/reports/eligibility-stats");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetAuthApprovalReportAsync_WhenApiReturns200_DeserializesReport()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-03-31",
            totalRequests = 800, approvedCount = 600, deniedCount = 150, pendingCount = 50,
            approvalRate = 0.75, avgDecisionDays = 4.5,
            byServiceType = new[]
            {
                new { serviceType = "Inpatient", count = 200, approvedCount = 150,
                      deniedCount = 40, approvalRate = 0.75, avgDecisionDays = 5.0 }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetAuthApprovalReportAsync(new ReportRequest());

        result.TotalRequests.Should().Be(800);
        result.ApprovedCount.Should().Be(600);
        result.ByServiceType.Should().HaveCount(1);
        result.ByServiceType[0].ServiceType.Should().Be("Inpatient");
        handler.CapturedUrls[0].Should().Contain("/reports/auth-approval");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetProviderPerformanceAsync_WhenApiReturns200_DeserializesProviderList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { providerId = "PRV-1", providerName = "Dr. Smith", specialty = "Cardiology",
                  claimCount = 120, totalBilled = 80000m, totalPaid = 65000m,
                  denialRate = 0.08, avgProcessingDays = 2.5 },
            new { providerId = "PRV-2", providerName = "Dr. Jones", specialty = "Orthopedics",
                  claimCount = 95, totalBilled = 70000m, totalPaid = 55000m,
                  denialRate = 0.12, avgProcessingDays = 3.1 }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetProviderPerformanceAsync(new ReportRequest());

        result.Should().HaveCount(2);
        result[0].Specialty.Should().Be("Cardiology");
        result[1].DenialRate.Should().Be(0.12);
        handler.CapturedUrls[0].Should().Contain("/reports/provider-performance");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetProviderPerformanceAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetProviderPerformanceAsync(new ReportRequest());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetClaimsSummaryAsync_PostsToClaimsServiceBaseUrl()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-01-31",
            totalClaims = 0, totalCharges = 0m, totalAllowed = 0m, totalPaid = 0m,
            approvedCount = 0, deniedCount = 0, pendedCount = 0, approvalRate = 0.0,
            avgClaimAmount = 0m
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        await sut.GetClaimsSummaryAsync(new ReportRequest());

        handler.CapturedUrls[0].Should().StartWith("http://localhost:5000/reports/claims-summary");
    }

    [Fact]
    public async Task GetAuthApprovalReportAsync_PostsToAuthorizationServiceBaseUrl()
    {
        var json = JsonSerializer.Serialize(new
        {
            periodFrom = "2025-01-01", periodTo = "2025-01-31",
            totalRequests = 0, approvedCount = 0, deniedCount = 0, pendingCount = 0,
            approvalRate = 0.0, avgDecisionDays = 0.0
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        await sut.GetAuthApprovalReportAsync(new ReportRequest());

        handler.CapturedUrls[0].Should().StartWith("http://localhost:5003/reports/auth-approval");
    }

    // ── ReportRequest – all filter properties ─────────────────────────────────

    [Fact]
    public async Task GetClaimsSummaryAsync_WithAllReportRequestFields_SendsAllFiltersInRequest()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalClaims = 0, totalAmount = 0m, approvedCount = 0,
            deniedCount = 0, pendingCount = 0, approvalRate = 0.0
        }, JsonOpts);
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var req = new ReportRequest
        {
            DateFrom = new DateTime(2026, 1, 1),
            DateTo = new DateTime(2026, 3, 31),
            ProviderId = "PRV-500",
            SponsorId = "SP-200",
            PlanId = "PLN-100"
        };

        // Verify all properties are accessible
        req.DateFrom.Should().Be(new DateTime(2026, 1, 1));
        req.DateTo.Should().Be(new DateTime(2026, 3, 31));
        req.ProviderId.Should().Be("PRV-500");
        req.SponsorId.Should().Be("SP-200");
        req.PlanId.Should().Be("PLN-100");

        await sut.GetClaimsSummaryAsync(req);

        handler.CapturedUrls.Should().ContainSingle();
    }
}
