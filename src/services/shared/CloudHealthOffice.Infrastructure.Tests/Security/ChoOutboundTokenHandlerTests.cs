using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

public class ChoOutboundTokenHandlerTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>The outbound allowlist every test host starts from.</summary>
    private static readonly Dictionary<string, string?> DefaultHostConfig = new()
    {
        ["Services:MemberService"] = "http://member-service/api/v1",
        ["Services:TokenService"] = "http://token-service:8080",
        ["Services:Nppes"] = "https://npiregistry.cms.hhs.gov/api/",
        ["ChoAuth:Outbound:ExcludedServices:0"] = "Nppes",
        ["ChoAuth:Outbound:Hosts:0"] = "claims-service",
        ["ChoAuth:Outbound:Suffixes:0"] = ".cho-staging.svc.cluster.local",
    };

    private static (HttpMessageInvoker, Capture) Build(
        HttpContext? http, bool withServiceToken,
        IDictionary<string, string?>? hostConfig = null, string environment = "Production")
    {
        var services = new ServiceCollection();
        var options = new ChoAuthOptions { Audience = ChoDevelopmentAuth.Audience };
        if (withServiceToken)
        {
            options.ServiceToken = new ChoServiceTokenOptions
            {
                Issuer = ChoDevelopmentAuth.ServiceIssuer, ClientId = "claims-service",
                SymmetricKey = ChoDevelopmentAuth.SymmetricKey,
            };
            services.AddSingleton(ChoDevelopmentAuth.ServiceTokenIssuer());
        }
        services.AddSingleton(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(hostConfig ?? DefaultHostConfig).Build();
        services.AddSingleton(new ChoOutboundHosts(configuration, new Env(environment)));

        var capture = new Capture();
        var handler = new ChoOutboundTokenHandler(
            new HttpContextAccessor { HttpContext = http }, services.BuildServiceProvider(),
            NullLogger<ChoOutboundTokenHandler>.Instance)
        { InnerHandler = capture };
        return (new HttpMessageInvoker(handler), capture);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static DefaultHttpContext UserContext(string tenant = "tenant-a")
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer user-token";
        http.Items["TenantId"] = tenant;
        return http;
    }

    private static async Task<HttpRequestMessage> SendAsync(HttpMessageInvoker invoker, Capture capture, string url, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (tenant != null) request.Headers.Add("X-Tenant-ID", tenant);
        await invoker.SendAsync(request, default);
        return capture.Last!;
    }

    // ── Which hosts are CHO ────────────────────────────────────────────────

    [Theory]
    [InlineData("http://member-service/api/v1/members")]        // Services:MemberService, dot-less
    [InlineData("http://claims-service:8080/api/claims")]       // ChoAuth:Outbound:Hosts
    [InlineData("http://tenant-service.cloudhealthoffice/api")] // default suffix
    [InlineData("http://rfai-service.cloudhealthoffice.svc.cluster.local/")] // default suffix
    [InlineData("http://claims-service.cho-staging.svc.cluster.local/")]     // configured suffix
    public async Task ConfiguredChoHost_ForwardsCallerToken(string url)
    {
        var (invoker, capture) = Build(UserContext(), withServiceToken: true);

        var sent = await SendAsync(invoker, capture, url);

        sent.Headers.Authorization!.Parameter.Should().Be("user-token");
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-a");
    }

    [Fact]
    public async Task ServicesHost_WithoutCaller_GetsServiceToken()
    {
        var (invoker, capture) = Build(http: null, withServiceToken: true);

        var sent = await SendAsync(invoker, capture, "http://member-service/api/v1/members", tenant: "tenant-a");

        new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization!.Parameter)
            .Claims.Single(c => c.Type == "tenant_id").Value.Should().Be("tenant-a");
    }

    [Theory]
    [InlineData("http://localhost:5001/api/claims")]
    [InlineData("http://127.0.0.1:8080/api/claims")]
    [InlineData("http://10.0.0.12/api/claims")]
    [InlineData("http://[::1]:5001/api/claims")]
    [InlineData("https://api.anthropic.com/v1/messages")]
    [InlineData("http://wiremock/api")]                  // dot-less, listed nowhere
    [InlineData("http://vault.vault.svc.cluster.local/")] // another namespace
    [InlineData("http://token-service:8080/v1/token")]    // Services:TokenService, excluded by default
    [InlineData("https://npiregistry.cms.hhs.gov/api/")]  // Services:Nppes, excluded by config
    [InlineData("http://member-service.evil.example/")]   // a CHO name as a label of an external host
    public async Task NonChoHost_GetsNoTokenAndNoTenant_WithCaller(string url)
    {
        var (invoker, capture) = Build(UserContext(), withServiceToken: true);

        var sent = await SendAsync(invoker, capture, url);

        sent.Headers.Authorization.Should().BeNull();
        sent.Headers.Contains("X-Tenant-ID").Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:5001/api/claims")]
    [InlineData("http://127.0.0.1:8080/api/claims")]
    [InlineData("http://[::1]:5001/api/claims")]
    [InlineData("http://wiremock/api")]
    [InlineData("http://token-service:8080/v1/token")]
    public async Task NonChoHost_GetsNoServiceToken_WithoutCaller(string url)
    {
        var (invoker, capture) = Build(http: null, withServiceToken: true);

        var sent = await SendAsync(invoker, capture, url, tenant: "tenant-a");

        sent.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ServicesEntryPointingAtLoopback_DoesNotMakeLoopbackChoHost()
    {
        var (invoker, capture) = Build(UserContext(), withServiceToken: true, new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://localhost:5001",
            ["Services:MemberService"] = "http://[::1]:5003",
            ["Services:CoverageService"] = "http://127.0.0.1:5009",
        }, environment: "Development");

        (await SendAsync(invoker, capture, "http://localhost:5001/api")).Headers.Authorization.Should().BeNull();
        (await SendAsync(invoker, capture, "http://[::1]:5003/api")).Headers.Authorization.Should().BeNull();
        (await SendAsync(invoker, capture, "http://127.0.0.1:5009/api")).Headers.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public async Task DevelopmentHosts_HonouredOnlyInDevelopmentAndTesting(string environment, bool attached)
    {
        var (invoker, capture) = Build(UserContext(), withServiceToken: true, new Dictionary<string, string?>
        {
            ["ChoAuth:Outbound:DevelopmentHosts:0"] = "localhost",
        }, environment);

        var sent = await SendAsync(invoker, capture, "http://localhost:5001/api");

        (sent.Headers.Authorization != null).Should().Be(attached);
    }

    [Fact]
    public async Task ExplicitlyListedLoopback_IsChoHost()
    {
        var (invoker, capture) = Build(UserContext(), withServiceToken: true, new Dictionary<string, string?>
        {
            ["ChoAuth:Outbound:Hosts:0"] = "[::1]",
        });

        (await SendAsync(invoker, capture, "http://[::1]:5001/api")).Headers.Authorization.Should().NotBeNull();
    }

    [Fact]
    public async Task NoHostPolicyRegistered_AttachesNothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ChoAuthOptions());
        var capture = new Capture();
        var handler = new ChoOutboundTokenHandler(
            new HttpContextAccessor { HttpContext = UserContext() }, services.BuildServiceProvider(),
            NullLogger<ChoOutboundTokenHandler>.Instance) { InnerHandler = capture };

        var sent = await SendAsync(new HttpMessageInvoker(handler), capture, "http://member-service.cloudhealthoffice/api");

        sent.Headers.Authorization.Should().BeNull();
    }

    // ── What is attached ───────────────────────────────────────────────────

    [Fact]
    public async Task ForwardsInboundUserToken_AndEchoesAuthenticatedTenant()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer user-token";
        http.Items["TenantId"] = "tenant-a";
        var (invoker, capture) = Build(http, withServiceToken: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service.cloudhealthoffice/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-b");
        await invoker.SendAsync(request, default);

        capture.Last!.Headers.Authorization!.Parameter.Should().Be("user-token");
        capture.Last.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-a");
    }

    [Fact]
    public async Task WithoutUserContext_MintsServiceTokenForNamedTenant()
    {
        var (invoker, capture) = Build(http: null, withServiceToken: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-a");
        await invoker.SendAsync(request, default);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(capture.Last!.Headers.Authorization!.Parameter);
        jwt.Claims.Single(c => c.Type == "tenant_id").Value.Should().Be("tenant-a");
        jwt.Claims.Single(c => c.Type == "sub").Value.Should().Be("claims-service");
        jwt.Audiences.Should().ContainSingle(ChoDevelopmentAuth.Audience);
    }

    [Fact]
    public async Task NeverSendsTokensToExternalHosts()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        http.Request.Headers.Authorization = "Bearer user-token";
        var (invoker, capture) = Build(http, withServiceToken: true);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"), default);

        capture.Last!.Headers.Authorization.Should().BeNull();
    }

    private static DefaultHttpContext ApiKeyContext()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client", "acme")], "ProviderApiKey"));
        http.Request.Headers["X-Api-Key"] = "k";
        http.Items["TenantId"] = "tenant-a";
        return http;
    }

    [Fact]
    public async Task ApiKeyCaller_IsNeverGivenTheServiceToken()
    {
        var (invoker, capture) = Build(ApiKeyContext(), withServiceToken: true);

        var sent = await SendAsync(invoker, capture, "http://member-service/api/v1/members", tenant: "tenant-a");

        sent.Headers.Authorization.Should().BeNull("an API-key caller holds no CHO token to forward");
    }

    [Fact]
    public async Task ApiKeyCaller_GetsTheServiceToken_OnlyWhenTheRequestAsksForIt()
    {
        var (invoker, capture) = Build(ApiKeyContext(), withServiceToken: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/v1/members").UseChoServiceToken();
        request.Headers.Add("X-Tenant-ID", "tenant-a");
        await invoker.SendAsync(request, default);

        new JwtSecurityTokenHandler().ReadJwtToken(capture.Last!.Headers.Authorization!.Parameter)
            .Claims.Single(c => c.Type == "azp").Value.Should().Be("claims-service");
    }

    [Fact]
    public async Task AnonymousInboundRequest_IsTreatedLikeBackgroundWork()
    {
        // e.g. a signed webhook: no authenticated caller to stand in for.
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "webhook-shared-secret";
        var (invoker, capture) = Build(http, withServiceToken: true);

        var sent = await SendAsync(invoker, capture, "http://member-service/api/v1/members", tenant: "tenant-a");

        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().NotBe("webhook-shared-secret");
    }

    [Fact]
    public async Task NoUserAndNoServiceToken_SendsNoCredentials()
    {
        var (invoker, capture) = Build(http: null, withServiceToken: false);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://member-service/api/members");
        request.Headers.Add("X-Tenant-ID", "tenant-a");
        await invoker.SendAsync(request, default);

        capture.Last!.Headers.Authorization.Should().BeNull();
    }
}
