using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class AppealsServiceTests
{
    private readonly Mock<ILogger<AppealsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public AppealsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:AppealsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private AppealsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AppealsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchAppealsAsync());
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchAppealsAsync(appealId: "APL-2026-0001"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_FilterByMemberId_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchAppealsAsync(memberId: "MBR-8201"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAppealByIdAsync("APL-2026-0001"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.Message.Should().Contain("Appeals Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
