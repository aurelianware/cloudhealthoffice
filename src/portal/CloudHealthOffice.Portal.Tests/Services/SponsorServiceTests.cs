using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class SponsorServiceTests
{
    private readonly Mock<ILogger<SponsorService>> _logger = new();
    private readonly IConfiguration _configuration;

    public SponsorServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:SponsorService"] = "http://localhost:5007"
            })
            .Build();
    }

    private SponsorService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new SponsorService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchSponsorsAsync ──

    [Fact]
    public async Task SearchSponsorsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchSponsorsAsync("Acme"));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── GetSponsorByIdAsync ──

    [Fact]
    public async Task GetSponsorByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetSponsorByIdAsync("SP-001"));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── CreateSponsorAsync ──

    [Fact]
    public async Task CreateSponsorAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateSponsorAsync(new CreateSponsorRequest()));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── UpdateSponsorAsync ──

    [Fact]
    public async Task UpdateSponsorAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateSponsorAsync("SP-001", new UpdateSponsorRequest()));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    [Fact]
    public async Task SearchSponsorsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchSponsorsAsync("Acme"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
