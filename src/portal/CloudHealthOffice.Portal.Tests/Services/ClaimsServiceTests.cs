using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ClaimsServiceTests
{
    private readonly Mock<ILogger<ClaimsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ClaimsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private ClaimsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ClaimsService(httpClient, _configuration, _logger.Object);
    }

    // ── GetRecentClaimsAsync ──

    [Fact]
    public async Task GetRecentClaimsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentClaimsAsync(10));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetRecentClaimsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentClaimsAsync(10));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ── GetClaimByIdAsync ──

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetClaimByIdAsync("CLM-2026-00001"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── SubmitClaimAsync ──

    [Fact]
    public async Task SubmitClaimAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SubmitClaimAsync(new SubmitClaimRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── SearchClaimsAsync ──

    [Fact]
    public async Task SearchClaimsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchClaimsAsync(new ClaimSearchRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── UpdateClaimStatusAsync ──

    [Fact]
    public async Task UpdateClaimStatusAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateClaimStatusAsync("CLM-2026-00001", "Denied"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetAdjudicationDataAsync ──

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAdjudicationDataAsync("CLM-2026-00001"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetAdjudicationDataAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAdjudicationDataAsync("CLM-2026-00001"));
        ex.Message.Should().Contain("Claims Service");
    }
}
