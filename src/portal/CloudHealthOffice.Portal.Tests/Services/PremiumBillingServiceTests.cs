using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class PremiumBillingServiceTests
{
    private readonly Mock<ILogger<PremiumBillingService>> _logger = new();
    private readonly IConfiguration _configuration;

    public PremiumBillingServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:BillingService"] = "http://localhost:5010"
            })
            .Build();
    }

    private PremiumBillingService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new PremiumBillingService(httpClient, _configuration, _logger.Object);
    }

    // ── GetBillingCyclesAsync ──

    [Fact]
    public async Task GetBillingCyclesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBillingCyclesAsync());
        ex.ServiceName.Should().Be("Billing Service");
    }

    [Fact]
    public async Task GetBillingCyclesAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetBillingCyclesAsync(sponsorId: "SP-001", status: "Open"));
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── GetBillingCycleByIdAsync ──

    [Fact]
    public async Task GetBillingCycleByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetBillingCycleByIdAsync("CYC-001"));
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── GenerateInvoiceAsync ──

    [Fact]
    public async Task GenerateInvoiceAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GenerateInvoiceAsync(new CreateInvoiceRequest()));
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── GetPremiumRatesAsync ──

    [Fact]
    public async Task GetPremiumRatesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetPremiumRatesAsync());
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── UpdatePremiumRateAsync ──

    [Fact]
    public async Task UpdatePremiumRateAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdatePremiumRateAsync("RATE-001", 125.50m, DateTime.Today));
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── MarkCycleAsPaidAsync ──

    [Fact]
    public async Task MarkCycleAsPaidAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.MarkCycleAsPaidAsync("CYC-001", DateTime.Today));
        ex.ServiceName.Should().Be("Billing Service");
    }

    // ── DownloadInvoiceAsync ──

    [Fact]
    public async Task DownloadInvoiceAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadInvoiceAsync("CYC-001"));
        ex.ServiceName.Should().Be("Billing Service");
    }

    [Fact]
    public async Task GetBillingCyclesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBillingCyclesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── GetBillingCyclesAsync ──

    [Fact]
    public async Task GetBillingCyclesAsync_WhenApiReturns200_DeserializesBillingCycleList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { cycleId = "CYC-1", sponsorId = "SP-10", sponsorName = "Acme Corp",
                  billingPeriod = "2025-03", billingFrequency = "Monthly",
                  dueDate = "2025-04-01", totalPremium = 85000.00m,
                  status = "Sent", memberCount = 200, invoiceNumber = "INV-0001" },
            new { cycleId = "CYC-2", sponsorId = "SP-20", sponsorName = "Beta Union",
                  billingPeriod = "2025-03", billingFrequency = "Monthly",
                  dueDate = "2025-04-01", totalPremium = 42500.00m,
                  status = "Paid", memberCount = 100, invoiceNumber = "INV-0002" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBillingCyclesAsync();

        result.Should().HaveCount(2);
        result[0].CycleId.Should().Be("CYC-1");
        result[0].SponsorName.Should().Be("Acme Corp");
        result[0].TotalPremium.Should().Be(85000.00m);
        result[0].BillingFrequency.Should().Be("Monthly");
        result[0].MemberCount.Should().Be(200);
        result[1].Status.Should().Be("Paid");
    }

    [Fact]
    public async Task GetBillingCyclesAsync_WithSponsorIdFilter_IncludesSponsorIdInUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBillingCyclesAsync(sponsorId: "SP-10");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("sponsorId=SP-10");
    }

    [Fact]
    public async Task GetBillingCyclesAsync_WithoutSponsorId_DoesNotAppendQueryParam()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBillingCyclesAsync();

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().NotContain("sponsorId=");
    }

    [Fact]
    public async Task GetBillingCyclesAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetBillingCyclesAsync();

        result.Should().BeEmpty();
    }

    // ── GetBillingCycleByIdAsync ──

    [Fact]
    public async Task GetBillingCycleByIdAsync_WhenApiReturns200_DeserializesDetailsWithLineItems()
    {
        var json = JsonSerializer.Serialize(new
        {
            cycleId = "CYC-1", sponsorId = "SP-10", sponsorName = "Acme Corp",
            billingPeriod = "2025-03", billingFrequency = "Monthly",
            dueDate = "2025-04-01", totalPremium = 85000m, status = "Sent",
            memberCount = 200, invoiceNumber = "INV-0001",
            taxAmount = 0m, adjustmentAmount = -500m, notes = "Credit applied",
            lineItems = new[]
            {
                new { planId = "PLN-1", planName = "Gold PPO", coverageLevel = "Employee",
                      memberCount = 120, unitRate = 450m, subTotal = 54000m },
                new { planId = "PLN-2", planName = "Silver HMO", coverageLevel = "Family",
                      memberCount = 80, unitRate = 387.50m, subTotal = 31000m }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBillingCycleByIdAsync("CYC-1");

        result.Should().NotBeNull();
        result!.CycleId.Should().Be("CYC-1");
        result.AdjustmentAmount.Should().Be(-500m);
        result.Notes.Should().Be("Credit applied");
        result.LineItems.Should().HaveCount(2);
        result.LineItems[0].PlanName.Should().Be("Gold PPO");
        result.LineItems[0].UnitRate.Should().Be(450m);
        result.LineItems[1].CoverageLevel.Should().Be("Family");
    }

    [Fact]
    public async Task GetBillingCycleByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetBillingCycleByIdAsync("CYC-NONE");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBillingCycleByIdAsync_UrlContainsCycleId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "null");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBillingCycleByIdAsync("CYC-42");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("/billing-runs/CYC-42");
    }

    // ── GenerateInvoiceAsync ──

    [Fact]
    public async Task GenerateInvoiceAsync_WhenApiReturns200_ExtractsCycleId()
    {
        var json = JsonSerializer.Serialize(new { cycleId = "CYC-NEW-7" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GenerateInvoiceAsync(new CreateInvoiceRequest
        {
            SponsorId = "SP-10", BillingPeriod = "2025-04",
            DueDate = new DateTime(2025, 5, 1), Notes = "April billing"
        });

        result.Should().Be("CYC-NEW-7");
    }

    [Fact]
    public async Task GenerateInvoiceAsync_VerifyPostSendsRequestBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { cycleId = "CYC-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.GenerateInvoiceAsync(new CreateInvoiceRequest
        {
            SponsorId = "SP-10", BillingPeriod = "2025-04",
            DueDate = new DateTime(2025, 5, 1), Notes = "Test invoice"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/billing-runs");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("SP-10");
        body.Should().Contain("2025-04");
    }

    // ── GetPremiumRatesAsync ──

    [Fact]
    public async Task GetPremiumRatesAsync_WhenApiReturns200_DeserializesPremiumRateList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { rateId = "RATE-1", planId = "PLN-1", planName = "Gold PPO",
                  coverageLevel = "Employee", ageBand = "30-39",
                  rate = 450m, effectiveDate = "2025-01-01" },
            new { rateId = "RATE-2", planId = "PLN-1", planName = "Gold PPO",
                  coverageLevel = "Family", ageBand = (string?)null,
                  rate = 1200m, effectiveDate = "2025-01-01" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetPremiumRatesAsync();

        result.Should().HaveCount(2);
        result[0].RateId.Should().Be("RATE-1");
        result[0].Rate.Should().Be(450m);
        result[0].AgeBand.Should().Be("30-39");
        result[1].CoverageLevel.Should().Be("Family");
        result[1].AgeBand.Should().BeNull();
    }

    [Fact]
    public async Task GetPremiumRatesAsync_WithPlanIdFilter_IncludesPlanIdInUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetPremiumRatesAsync(planId: "PLN-5");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("planId=PLN-5");
    }

    [Fact]
    public async Task GetPremiumRatesAsync_WithoutPlanId_DoesNotAppendQueryParam()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetPremiumRatesAsync();

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().NotContain("planId=");
    }

    // ── UpdatePremiumRateAsync ──

    [Fact]
    public async Task UpdatePremiumRateAsync_WhenApiReturns200_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdatePremiumRateAsync("RATE-1", 475.00m, new DateTime(2025, 7, 1));

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/premium-invoices/RATE-1");
    }

    [Fact]
    public async Task UpdatePremiumRateAsync_SendsRateAndEffectiveDateInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdatePremiumRateAsync("RATE-1", 475.00m, new DateTime(2025, 7, 1));

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("475");
        body.Should().Contain("2025");
    }

    // ── MarkCycleAsPaidAsync ──

    [Fact]
    public async Task MarkCycleAsPaidAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.MarkCycleAsPaidAsync("CYC-1", new DateTime(2025, 4, 15));

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/billing-runs/CYC-1/mark-paid");
    }

    [Fact]
    public async Task MarkCycleAsPaidAsync_SendsPaidDateInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.MarkCycleAsPaidAsync("CYC-1", new DateTime(2025, 4, 15));

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("2025");
    }

    // ── DownloadInvoiceAsync ──

    [Fact]
    public async Task DownloadInvoiceAsync_WhenApiReturns200_ReturnsStream()
    {
        var content = "%PDF-1.4 mock invoice content";
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(content, Encoding.UTF8, "application/pdf");
            return response;
        });
        var sut = CreateService(new HttpClient(handler));

        var stream = await sut.DownloadInvoiceAsync("CYC-1");

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        text.Should().Contain("PDF");
        handler.CapturedUrls[0].Should().Contain("/billing-runs/CYC-1/invoice");
    }
}
