using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class AuthorizationServiceTests
{
    private readonly Mock<ILogger<AuthorizationService>> _logger = new();
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly IConfiguration _configuration;

    public AuthorizationServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:AuthorizationService"] = "http://localhost:5003"
            })
            .Build();

        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("fake-token");
    }

    private AuthorizationService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AuthorizationService(httpClient, _configuration, _logger.Object, _tokenAcquisition.Object);
    }

    // ── GetAuthorizationsAsync ──

    [Fact]
    public async Task GetAuthorizationsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAuthorizationsAsync());
        ex.ServiceName.Should().Be("Authorization Service");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_WithMemberId_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAuthorizationsAsync(memberId: "MBR-001"));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    // ── GetAuthorizationByIdAsync ──

    [Fact]
    public async Task GetAuthorizationByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAuthorizationByIdAsync("AUTH-001"));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    // ── SubmitAuthorizationAsync ──

    [Fact]
    public async Task SubmitAuthorizationAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SubmitAuthorizationAsync(new SubmitAuthorizationRequest()));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAuthorizationsAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
