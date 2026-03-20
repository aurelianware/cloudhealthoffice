using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EnrollmentOperationsServiceTests
{
    private readonly Mock<ILogger<EnrollmentOperationsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EnrollmentOperationsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private EnrollmentOperationsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EnrollmentOperationsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetTodaySummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetTodaySummaryAsync());
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetRecentFilesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentFilesAsync());
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetFileDetailAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetFileDetailAsync("ENR-FILE-001"));
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetTodaySummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetTodaySummaryAsync());
        ex.Message.Should().Contain("Member Service");
    }

    [Fact]
    public async Task GetRecentFilesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentFilesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
