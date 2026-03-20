using System.Net;
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
}
