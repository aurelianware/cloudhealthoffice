using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ReferenceDataServiceTests
{
    private readonly Mock<ILogger<ReferenceDataService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ReferenceDataServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ReferenceDataService"] = "http://localhost:5011"
            })
            .Build();
    }

    private ReferenceDataService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ReferenceDataService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchCodesAsync ──

    [Fact]
    public async Task SearchCodesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchCodesAsync());
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    [Fact]
    public async Task SearchCodesAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchCodesAsync(codeSystem: "ICD-10", searchTerm: "diabetes"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeDetailsAsync ──

    [Fact]
    public async Task GetCodeDetailsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCodeDetailsAsync("ICD-10", "E11.9"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeSystemsAsync ──

    [Fact]
    public async Task GetCodeSystemsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetCodeSystemsAsync());
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeUsageStatsAsync ──

    [Fact]
    public async Task GetCodeUsageStatsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCodeUsageStatsAsync("ICD-10", "E11.9"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    [Fact]
    public async Task SearchCodesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchCodesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
