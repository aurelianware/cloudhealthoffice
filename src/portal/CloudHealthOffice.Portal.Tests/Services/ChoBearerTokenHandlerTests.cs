using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ChoBearerTokenHandlerTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://claims-service.cloudhealthoffice/api",
            ["Services:MemberService"] = "http://localhost:5003/api",
            ["Services:TenantService"] = "http://tenant-service.cloudhealthoffice/api",
            ["Services:TokenService"] = "http://localhost:5030",
            ["Services:ArgoWorkflows"] = "http://argo-workflows-server.cho-workflows:2746",
            ["Services:Prometheus"] = "http://cho-monitoring-prometheus-server.cho-monitoring",
            ["Services:ClaimsServiceTenantId"] = "not-a-url",
        })
        .Build();

    private readonly Mock<IChoTokenProvider> _tokens = new();
    private readonly FakeHandler _backend;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public ChoBearerTokenHandlerTests()
    {
        _backend = new FakeHandler(r => _respond(r));
        _tokens.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token("cho-token-1", "tenant-1")));
    }

    private static ChoTokenExchangeResponse Token(string accessToken, string tenantId)
        => new() { AccessToken = accessToken, TenantId = tenantId, ExpiresIn = 3600 };

    private HttpClient CreateClient(IChoTokenProvider? tokens = null)
        => new(new ChoBearerTokenHandler(tokens ?? _tokens.Object, new ChoServiceHosts(Configuration),
            NullLogger<ChoBearerTokenHandler>.Instance) { InnerHandler = _backend });

    // ── Which hosts get the token ──

    [Theory]
    [InlineData("http://claims-service.cloudhealthoffice/api/claims/search")]
    [InlineData("http://CLAIMS-SERVICE.cloudhealthoffice/api/claims")]
    [InlineData("http://localhost:5003/api/members/M1")]
    [InlineData("http://tenant-service.cloudhealthoffice/api/v1/tenants/t/users")]
    public async Task ConfiguredChoService_GetsBearerAndTokenTenant(string url)
    {
        var client = CreateClient();

        await client.GetAsync(url);

        var sent = _backend.CapturedRequests.Should().ContainSingle().Subject;
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("cho-token-1");
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-1");
    }

    [Theory]
    [InlineData("https://api.stripe.com/v1/customers")]
    [InlineData("https://graph.microsoft.com/v1.0/me")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/token")]
    [InlineData("https://www.example.com/anything")]
    [InlineData("http://localhost:5030/v1/token/exchange")] // the token service itself
    [InlineData("http://localhost:5099/api/claims")] // same host, unconfigured port
    [InlineData("http://argo-workflows-server.cho-workflows:2746/api/v1/workflows")]
    [InlineData("http://cho-monitoring-prometheus-server.cho-monitoring/api/v1/query")]
    [InlineData("http://claims-service.other-namespace/api/claims")] // not the configured host
    [InlineData("https://claims-service.cloudhealthoffice/api/claims")] // https → port 443, not configured
    public async Task NonChoHost_GetsNoTokenAndNoTenantHeader(string url)
    {
        var client = CreateClient();

        await client.GetAsync(url);

        var sent = _backend.CapturedRequests.Should().ContainSingle().Subject;
        sent.Headers.Authorization.Should().BeNull();
        sent.Headers.Contains("X-Tenant-ID").Should().BeFalse();
        _tokens.Verify(t => t.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NonChoHost_KeepsItsOwnAuthorization()
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/customers");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk_test_123");

        await client.SendAsync(request);

        _backend.CapturedRequests.Single().Headers.Authorization!.Parameter.Should().Be("sk_test_123");
    }

    [Fact]
    public async Task ChoService_PresetTenantHeaderAndAuthorization_AreReplacedByTheTokens()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "some-other-tenant");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "entra-token");

        await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");

        var sent = _backend.CapturedRequests.Single();
        sent.Headers.Authorization!.Parameter.Should().Be("cho-token-1");
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-1");
    }

    [Fact]
    public async Task ChoService_WithoutAToken_IsNotSent()
    {
        _tokens.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(ChoTokenStatus.NoAccess));
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<ChoTokenUnavailableException>(
            () => client.GetAsync("http://claims-service.cloudhealthoffice/api/claims"));

        ex.Status.Should().Be(ChoTokenStatus.NoAccess);
        ex.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _backend.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingToken_SurfacesAsServiceUnavailableThroughExistingServiceClients()
    {
        _tokens.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(ChoTokenStatus.Unavailable));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AuthorizationService"] = "http://claims-service.cloudhealthoffice/api",
        }).Build();
        var service = new AuthorizationService(CreateClient(), configuration, NullLogger<AuthorizationService>.Instance);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => service.GetAuthorizationsAsync());
        _backend.CapturedRequests.Should().BeEmpty();
    }

    // ── 401 → refresh and retry once ──

    [Fact]
    public async Task Unauthorized_DropsTheTokenAndRetriesOnceWithAFreshOne()
    {
        _tokens.SetupSequence(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token("stale-token", "tenant-1")))
            .ReturnsAsync(ChoTokenResult.Success(Token("fresh-token", "tenant-1")));
        _respond = r => r.Headers.Authorization!.Parameter == "stale-token"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK);
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("http://claims-service.cloudhealthoffice/api/claims", new { a = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _backend.CapturedRequests.Should().HaveCount(2);
        _backend.CapturedRequests[1].Headers.Authorization!.Parameter.Should().Be("fresh-token");
        _tokens.Verify(t => t.Invalidate(It.Is<ChoTokenExchangeResponse>(x => x.AccessToken == "stale-token")), Times.Once);
    }

    [Fact]
    public async Task Unauthorized_Twice_IsReturnedAfterOneRetry()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var client = CreateClient();

        var response = await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _backend.CapturedRequests.Should().HaveCount(2, "exactly one retry");
    }

    [Fact]
    public async Task Unauthorized_WhenNoFreshTokenCanBeObtained_ReturnsTheOriginal401()
    {
        _tokens.SetupSequence(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token("stale-token", "tenant-1")))
            .ReturnsAsync(ChoTokenResult.Failure(ChoTokenStatus.NoAccess));
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var client = CreateClient();

        var response = await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _backend.CapturedRequests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherFailures_AreNotRetried(HttpStatusCode status)
    {
        _respond = _ => new HttpResponseMessage(status);
        var client = CreateClient();

        var response = await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");

        response.StatusCode.Should().Be(status);
        _backend.CapturedRequests.Should().ContainSingle();
        _tokens.Verify(t => t.Invalidate(It.IsAny<ChoTokenExchangeResponse>()), Times.Never);
    }

    [Fact]
    public async Task Unauthorized_WithAStreamedUpload_IsNotResent()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var client = CreateClient();
        using var content = new MultipartFormDataContent { { new StreamContent(new MemoryStream(new byte[] { 1, 2 })), "file", "a.pdf" } };

        var response = await client.PostAsync("http://claims-service.cloudhealthoffice/api/attachments/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _backend.CapturedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Unauthorized_WithTheRealProvider_ReExchangesAtTheTokenService()
    {
        // End to end: handler + ChoTokenProvider + a fake token service.
        var exchanges = 0;
        var tokenService = new FakeHandler(_ =>
        {
            exchanges++;
            return Json(HttpStatusCode.OK, ExchangeJson(accessToken: $"cho-token-{exchanges}"));
        });
        var tokenAcquisition = new Mock<ITokenAcquisition>();
        tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<System.Security.Claims.ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("entra-token");
        var providerConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TokenService"] = TokenServiceUrl,
            ["TokenService:Scope"] = Scope,
        }).Build();
        var provider = new ChoTokenProvider(
            new StaticAuthenticationStateProvider(EntraUser()),
            new SingleHandlerHttpClientFactory(tokenService),
            new MemoryCache(new MemoryCacheOptions()),
            providerConfig,
            new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance,
            tokenAcquisition.Object);
        var sentTokens = new List<string?>();
        _respond = r =>
        {
            sentTokens.Add(r.Headers.Authorization?.Parameter); // the retry re-sends the same message
            return r.Headers.Authorization!.Parameter == "cho-token-1"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        };
        var client = CreateClient(provider);

        var first = await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");
        var second = await client.GetAsync("http://claims-service.cloudhealthoffice/api/claims");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        exchanges.Should().Be(2, "the rejected token was dropped and the fresh one is then reused");
        sentTokens.Should().Equal("cho-token-1", "cho-token-2", "cho-token-2");
    }
}
