using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Self-service signup goes to the token service with the user's Entra token;
/// the portal sends only the form's fields (no directory, status, demo flag or
/// admin list) and writes nothing to the tenant database.
/// </summary>
public class ChoTokenProviderSignupTests
{
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly List<HttpRequestMessage> _calls = new();
    private readonly List<string> _bodies = new();
    private Func<HttpResponseMessage> _response =
        () => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"tenantId":"tenant-new"}""", System.Text.Encoding.UTF8, "application/json") };

    public ChoTokenProviderSignupTests()
    {
        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("entra-token");
    }

    private ChoTokenProvider CreateProvider()
    {
        var handler = new FakeHandler(request =>
        {
            _calls.Add(request);
            _bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return request.RequestUri!.AbsolutePath == "/v1/signup" ? _response() : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TokenService"] = TokenServiceUrl,
                ["TokenService:Scope"] = Scope,
                ["Authentication:Mode"] = "Entra",
            })
            .Build();
        return new ChoTokenProvider(
            new StaticAuthenticationStateProvider(EntraUser()),
            new SingleHandlerHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()), configuration, new TestHostEnvironment("Production"),
            new ListLogger<ChoTokenProvider>(), _tokenAcquisition.Object, null,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
    }

    [Fact]
    public async Task Signup_PostsOnlyTheFormFields_WithTheEntraToken()
    {
        var result = await CreateProvider().SignupAsync(new ChoSignupRequest
        {
            OrganizationName = "New Plan", Tier = "starter", StripeCustomerId = "cus_ABC12345",
        });

        result.Succeeded.Should().BeTrue();
        result.TenantId.Should().Be("tenant-new");
        var call = _calls.Should().ContainSingle().Subject;
        call.Headers.Authorization!.Parameter.Should().Be("entra-token");
        _bodies.Single().Should().Contain("New Plan")
            .And.NotContain("azureTenantId").And.NotContain("subscriptionStatus").And.NotContain("isDemo");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, ChoSignupStatus.AlreadySubscribed)]
    [InlineData(HttpStatusCode.BadRequest, ChoSignupStatus.InvalidRequest)]
    [InlineData(HttpStatusCode.Unauthorized, ChoSignupStatus.InvalidToken)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ChoSignupStatus.Unavailable)]
    public async Task Signup_Refusals_AreReported(HttpStatusCode status, ChoSignupStatus expected)
    {
        _response = () => new HttpResponseMessage(status);

        var result = await CreateProvider().SignupAsync(new ChoSignupRequest { OrganizationName = "X", Tier = "starter" });

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(expected);
    }
}
