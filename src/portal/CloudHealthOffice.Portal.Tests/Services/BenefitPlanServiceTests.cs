using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class BenefitPlanServiceTests
{
    private readonly Mock<ILogger<BenefitPlanService>> _logger = new();
    private readonly IConfiguration _configuration;

    public BenefitPlanServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:BenefitPlanService"] = "http://localhost:5002"
            })
            .Build();
    }

    private BenefitPlanService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new BenefitPlanService(httpClient, _configuration, _logger.Object);
    }

    // ── GetBenefitPlansAsync ──

    [Fact]
    public async Task GetBenefitPlansAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBenefitPlansAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── SearchBenefitPlansAsync ──

    [Fact]
    public async Task SearchBenefitPlansAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchBenefitPlansAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task SearchBenefitPlansAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchBenefitPlansAsync(sponsorId: "SP-001", productType: "PPO"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetBenefitPlanByIdAsync ──

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetBenefitPlanByIdAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── CreateBenefitPlanAsync ──

    [Fact]
    public async Task CreateBenefitPlanAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateBenefitPlanAsync(new CreateBenefitPlanRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateBenefitPlanAsync ──

    [Fact]
    public async Task UpdateBenefitPlanAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateBenefitPlanAsync("PLN-001", new UpdateBenefitPlanRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetAvailableBenefitsAsync ──

    [Fact]
    public async Task GetAvailableBenefitsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAvailableBenefitsAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetServiceBenefitRulesAsync ──

    [Fact]
    public async Task GetServiceBenefitRulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetServiceBenefitRulesAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateServiceBenefitRulesAsync ──

    [Fact]
    public async Task UpdateServiceBenefitRulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateServiceBenefitRulesAsync(new UpdateServiceBenefitRulesRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetAccumulatorConfigAsync ──

    [Fact]
    public async Task GetAccumulatorConfigAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorConfigAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateAccumulatorConfigAsync ──

    [Fact]
    public async Task UpdateAccumulatorConfigAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateAccumulatorConfigAsync("PLN-001", new AccumulatorConfiguration()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task GetBenefitPlansAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBenefitPlansAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
