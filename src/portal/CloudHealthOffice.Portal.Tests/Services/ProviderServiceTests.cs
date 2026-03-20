using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ProviderServiceTests
{
    private readonly Mock<ILogger<ProviderService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ProviderServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ProviderService"] = "http://localhost:5004"
            })
            .Build();
    }

    private ProviderService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ProviderService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchProvidersAsync (single string) ──

    [Fact]
    public async Task SearchProvidersAsync_ByTerm_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchProvidersAsync("Smith"));
        ex.ServiceName.Should().Be("Provider Service");
    }

    // ── SearchProvidersAsync (filtered) ──

    [Fact]
    public async Task SearchProvidersAsync_Filtered_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchProvidersAsync(specialty: "Cardiology"));
        ex.ServiceName.Should().Be("Provider Service");
    }

    // ── GetProviderByIdAsync ──

    [Fact]
    public async Task GetProviderByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetProviderByIdAsync("PRV-001"));
        ex.ServiceName.Should().Be("Provider Service");
    }

    // ── CreateProviderAsync ──

    [Fact]
    public async Task CreateProviderAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateProviderAsync(new CreateProviderRequest()));
        ex.ServiceName.Should().Be("Provider Service");
    }

    // ── UpdateProviderAsync ──

    [Fact]
    public async Task UpdateProviderAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateProviderAsync("PRV-001", new UpdateProviderRequest()));
        ex.ServiceName.Should().Be("Provider Service");
    }

    // ── GetSpecialtiesAsync ──

    [Fact]
    public async Task GetSpecialtiesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSpecialtiesAsync());
        ex.ServiceName.Should().Be("Provider Service");
    }

    [Fact]
    public async Task SearchProvidersAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchProvidersAsync("Smith"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
