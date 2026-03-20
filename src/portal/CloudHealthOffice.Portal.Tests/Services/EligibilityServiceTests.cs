using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EligibilityServiceTests
{
    private readonly Mock<ILogger<EligibilityService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EligibilityServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:EligibilityService"] = "http://localhost:5005"
            })
            .Build();
    }

    private EligibilityService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EligibilityService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task CheckEligibilityAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.ServiceName.Should().Be("Eligibility Service");
    }

    [Fact]
    public async Task CheckEligibilityAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task CheckEligibilityAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CheckEligibilityAsync(new { MemberId = "MBR-001" }));
        ex.Message.Should().Contain("Eligibility Service");
    }
}
