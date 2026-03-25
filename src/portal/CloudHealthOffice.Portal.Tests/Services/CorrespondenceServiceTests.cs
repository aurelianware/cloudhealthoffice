using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CorrespondenceServiceTests
{
    private readonly Mock<ILogger<CorrespondenceService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CorrespondenceServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private CorrespondenceService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CorrespondenceService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetQueueAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WithTypeFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueAsync(type: "EOB"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WithStatusFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueAsync(status: "Queued"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetOutstandingRfaisAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.Message.Should().Contain("Claims Service");
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetOutstandingRfaisAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
