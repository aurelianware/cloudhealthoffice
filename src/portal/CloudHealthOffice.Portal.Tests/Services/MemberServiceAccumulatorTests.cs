using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MemberServiceAccumulatorTests
{
    private readonly Mock<ILogger<MemberService>> _logger = new();
    private readonly IConfiguration _configuration;

    public MemberServiceAccumulatorTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private MemberService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new MemberService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetAccumulatorsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorsAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetAccumulatorsAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorsAsync("MBR-8201"));
        ex.Message.Should().Contain("Member Service");
    }

    [Fact]
    public async Task GetAccumulatorsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorsAsync("MBR-8201"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task GetAccumulatorsAsync_DifferentMemberId_StillThrows()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorsAsync("MBR-9999"));
        ex.ServiceName.Should().Be("Member Service");
    }
}
