using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CoverageServiceTests
{
    private readonly Mock<ILogger<CoverageService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CoverageServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://localhost:5009"
            })
            .Build();
    }

    private CoverageService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CoverageService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.ServiceName.Should().Be("Coverage Service");
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task GetCoverageByMemberIdAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageByMemberIdAsync("MBR-001"));
        ex.Message.Should().Contain("Coverage Service");
    }
}
