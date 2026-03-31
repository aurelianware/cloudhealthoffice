using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class PaymentRunServiceTests
{
    private readonly Mock<ILogger<PaymentRunService>> _logger = new();
    private readonly IConfiguration _configuration;

    public PaymentRunServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:PaymentService"] = "http://localhost:5006"
            })
            .Build();
    }

    private PaymentRunService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new PaymentRunService(httpClient, _configuration, _logger.Object);
    }

    // ── GetPaymentRunsAsync ──

    [Fact]
    public async Task GetPaymentRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetPaymentRunsAsync());
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── GetPaymentRunByIdAsync ──

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetPaymentRunByIdAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── CreatePaymentRunAsync ──

    [Fact]
    public async Task CreatePaymentRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreatePaymentRunAsync(new CreatePaymentRunRequest()));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── CancelPaymentRunAsync ──

    [Fact]
    public async Task CancelPaymentRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CancelPaymentRunAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── DownloadEraForRunAsync ──

    [Fact]
    public async Task DownloadEraForRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadEraForRunAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    [Fact]
    public async Task GetPaymentRunsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetPaymentRunsAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── GetPaymentRunsAsync ──

    [Fact]
    public async Task GetPaymentRunsAsync_WhenApiReturns200_DeserializesPaymentRunList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { runId = "RUN-1", runName = "Weekly Batch", lineOfBusiness = "Commercial",
                  status = "Completed", createdDate = "2025-03-01", createdBy = "admin",
                  claimCount = 150, processedCount = 148, totalAmount = 245000.50m },
            new { runId = "RUN-2", runName = "Monthly PPO", lineOfBusiness = "Medicare",
                  status = "Pending", createdDate = "2025-03-15", createdBy = "finance",
                  claimCount = 300, processedCount = 0, totalAmount = 0m }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetPaymentRunsAsync();

        result.Should().HaveCount(2);
        result[0].RunId.Should().Be("RUN-1");
        result[0].RunName.Should().Be("Weekly Batch");
        result[0].TotalAmount.Should().Be(245000.50m);
        result[0].ProcessedCount.Should().Be(148);
        result[1].Status.Should().Be("Pending");
    }

    [Fact]
    public async Task GetPaymentRunsAsync_PassesLimitAsQueryParam()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetPaymentRunsAsync(limit: 25);

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("limit=25");
    }

    [Fact]
    public async Task GetPaymentRunsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetPaymentRunsAsync();

        result.Should().BeEmpty();
    }

    // ── GetPaymentRunByIdAsync ──

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenApiReturns200_DeserializesDetailsWithClaims()
    {
        var json = JsonSerializer.Serialize(new
        {
            runId = "RUN-1", runName = "Weekly Batch", lineOfBusiness = "Commercial",
            status = "Completed", createdDate = "2025-03-01", createdBy = "admin",
            claimCount = 2, processedCount = 2, totalAmount = 5000m,
            claimServiceDateFrom = "2025-02-01", claimServiceDateTo = "2025-02-28",
            sponsorFilter = "SP-100", planFilter = "PLN-200",
            totalCharges = 6000m, totalAllowed = 5200m, totalMemberResponsibility = 200m,
            approvedCount = 1, deniedCount = 0, adjustmentCount = 1,
            claims = new[]
            {
                new { claimId = "CLM-1", claimNumber = "CN-0001", memberName = "John Doe",
                      providerName = "Dr. Smith", chargeAmount = 3000m, allowedAmount = 2600m,
                      paidAmount = 2500m, memberResponsibility = 100m, paymentStatus = "Included" },
                new { claimId = "CLM-2", claimNumber = "CN-0002", memberName = "Jane Doe",
                      providerName = "Dr. Jones", chargeAmount = 3000m, allowedAmount = 2600m,
                      paidAmount = 2500m, memberResponsibility = 100m, paymentStatus = "Adjusted" }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetPaymentRunByIdAsync("RUN-1");

        result.Should().NotBeNull();
        result!.RunId.Should().Be("RUN-1");
        result.TotalCharges.Should().Be(6000m);
        result.TotalAllowed.Should().Be(5200m);
        result.TotalMemberResponsibility.Should().Be(200m);
        result.ApprovedCount.Should().Be(1);
        result.AdjustmentCount.Should().Be(1);
        result.SponsorFilter.Should().Be("SP-100");
        result.Claims.Should().HaveCount(2);
        result.Claims[0].ClaimNumber.Should().Be("CN-0001");
        result.Claims[0].PaidAmount.Should().Be(2500m);
        result.Claims[1].PaymentStatus.Should().Be("Adjusted");
    }

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetPaymentRunByIdAsync("RUN-NONE");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPaymentRunByIdAsync_UrlContainsRunId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetPaymentRunByIdAsync("RUN-42");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("/paymentruns/RUN-42");
    }

    // ── CreatePaymentRunAsync ──

    [Fact]
    public async Task CreatePaymentRunAsync_WhenApiReturns200_ExtractsRunId()
    {
        var json = JsonSerializer.Serialize(new { runId = "RUN-NEW-99" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreatePaymentRunAsync(new CreatePaymentRunRequest
        {
            RunName = "Test Run", LineOfBusiness = "Commercial",
            ClaimServiceDateFrom = new DateTime(2025, 1, 1),
            ClaimServiceDateTo = new DateTime(2025, 1, 31)
        });

        result.Should().Be("RUN-NEW-99");
    }

    [Fact]
    public async Task CreatePaymentRunAsync_VerifyPostSendsRequestBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { runId = "RUN-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.CreatePaymentRunAsync(new CreatePaymentRunRequest
        {
            RunName = "Medicare Batch", LineOfBusiness = "Medicare",
            SponsorId = "SP-50", PlanId = "PLN-10"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/paymentruns");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Medicare Batch");
        body.Should().Contain("Medicare");
    }

    // ── CancelPaymentRunAsync ──

    [Fact]
    public async Task CancelPaymentRunAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.CancelPaymentRunAsync("RUN-5");

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/paymentruns/RUN-5/cancel");
    }

    // ── DownloadEraForRunAsync ──

    [Fact]
    public async Task DownloadEraForRunAsync_WhenApiReturns200_ReturnsStream()
    {
        var content = "ISA*00*          *00*          *ZZ*SENDER";
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(content, Encoding.UTF8, "application/octet-stream");
            return response;
        });
        var sut = CreateService(new HttpClient(handler));

        var stream = await sut.DownloadEraForRunAsync("RUN-1");

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        text.Should().Contain("ISA");
        handler.CapturedUrls[0].Should().Contain("/paymentruns/RUN-1/835");
    }

    // ── PaymentRunSummary – remaining properties ──────────────────────────────

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenApiReturns200_DeserializesAllPaymentRunSummaryProperties()
    {
        var json = JsonSerializer.Serialize(new
        {
            runId = "RUN-FULL", runName = "February Medicare Batch",
            lineOfBusiness = "Medicare", status = "Completed",
            createdDate = "2026-02-01T08:00:00Z",
            startedDate = "2026-02-01T09:00:00Z",
            completedDate = "2026-02-01T11:30:00Z",
            createdBy = "finance@healthplan.com",
            claimCount = 850, processedCount = 850,
            totalAmount = 425000m,
            errorMessage = (string?)null,
            eraFileUrl = "https://storage.example.com/era/RUN-FULL-835.txt"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetPaymentRunByIdAsync("RUN-FULL");

        result.Should().NotBeNull();
        result!.StartedDate.Should().NotBeNull();
        result.CompletedDate.Should().NotBeNull();
        result.ErrorMessage.Should().BeNull();
        result.EraFileUrl.Should().Be("https://storage.example.com/era/RUN-FULL-835.txt");
    }

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenRunHasError_DeserializesErrorMessage()
    {
        var json = JsonSerializer.Serialize(new
        {
            runId = "RUN-ERR", runName = "Failed March Run",
            lineOfBusiness = "Commercial", status = "Failed",
            createdDate = "2026-03-01T08:00:00Z",
            startedDate = "2026-03-01T09:00:00Z",
            completedDate = (string?)null,
            createdBy = "finance@healthplan.com",
            claimCount = 200, processedCount = 50, totalAmount = 0m,
            errorMessage = "Database connection timeout during ERA generation",
            eraFileUrl = (string?)null
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetPaymentRunByIdAsync("RUN-ERR");

        result.Should().NotBeNull();
        result!.StartedDate.Should().NotBeNull();
        result.CompletedDate.Should().BeNull();
        result.ErrorMessage.Should().Be("Database connection timeout during ERA generation");
        result.EraFileUrl.Should().BeNull();
    }
}
