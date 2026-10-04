using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ServiceProgram = CloudHealthOffice.ProviderVerificationService.Program;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Nppes;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Security;

/// <summary>
/// The real provider-verification-service pipeline. Every caller needs a CHO
/// token, the tenant comes from that token, each endpoint names its
/// permission, and calls to external data sources never carry a CHO token.
/// </summary>
public class ProviderVerificationPipelineAuthTests : IClassFixture<ProviderVerificationPipelineAuthTests.Factory>
{
    private const string Tenant = "tenant-a";
    private const string ValidNpi = "1234567893";

    public sealed class Factory : WebApplicationFactory<ServiceProgram>
    {
        public CapturingHandler Nppes { get; } = new();
        public CapturingHandler Probe { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("HealthChecks:EnableExternalNppesCheck", "false");
            builder.ConfigureServices(services =>
            {
                // The real NPPES adapter and its client pipeline; only the
                // network is replaced, so the captured request shows exactly
                // what would have left the service.
                services.AddHttpClient<INppesAdapter, NppesHttpAdapter>()
                    .ConfigurePrimaryHttpMessageHandler(() => Nppes);
                // A plain factory client: carries whatever every factory
                // client in this service gets by default.
                services.AddHttpClient("probe")
                    .ConfigurePrimaryHttpMessageHandler(() => Probe);
            });
        }
    }

    public sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public void Reset()
        {
            lock (_requests) _requests.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result_count\":0,\"results\":[]}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly Factory _factory;

    public ProviderVerificationPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Nppes.Reset();
        _factory.Probe.Reset();
    }

    private HttpClient UserClient(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(new { npis = new[] { "0000000000" } });
        return request;
    }

    public static TheoryData<string, string> ApiEndpoints => new()
    {
        { "GET", $"/api/v1/providers/{ValidNpi}/verify" },
        { "GET", $"/api/v1/providers/{ValidNpi}/nppes" },
        { "GET", "/api/v1/providers/search/nppes?lastName=Smith&state=TX" },
        { "GET", $"/api/v1/providers/{ValidNpi}/integrity-score" },
        { "POST", "/api/v1/providers/verify/batch" },
    };

    public static TheoryData<string, string> ReadEndpoints => new()
    {
        { "GET", $"/api/v1/providers/{ValidNpi}/verify" },
        { "GET", $"/api/v1/providers/{ValidNpi}/nppes" },
        { "GET", "/api/v1/providers/search/nppes?lastName=Smith&state=TX" },
        { "GET", $"/api/v1/providers/{ValidNpi}/integrity-score" },
    };

    // ── Defect: no authentication at all ──────────────────────────

    [Theory]
    [MemberData(nameof(ApiEndpoints))]
    public async Task NoToken_HeaderTenantOnly_Returns401(string method, string path)
    {
        var client = _factory.CreateClient();
        using var request = Request(method, path);
        request.Headers.Add("X-Tenant-ID", Tenant);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Nppes.Requests);
    }

    [Fact]
    public async Task HeaderTenantDisagreeingWithToken_Returns403()
    {
        var client = _factory.CreateClient();
        using var request = Request("GET", $"/api/v1/providers/{ValidNpi}/integrity-score");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ProviderRelations));
        request.Headers.Add("X-Tenant-ID", "tenant-b");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Defect: no permission check ───────────────────────────────

    [Theory]
    [MemberData(nameof(ApiEndpoints))]
    public async Task RoleWithoutProvidersPermissions_Returns403(string method, string path)
    {
        // UMCoordinator holds no providers:* permission.
        var client = UserClient(ChoRolePermissions.UMCoordinator);
        using var request = Request(method, path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Nppes.Requests);
    }

    [Theory]
    [MemberData(nameof(ReadEndpoints))]
    public async Task ProvidersRead_CanCallReadEndpoints(string method, string path)
    {
        // ClaimsExaminer holds providers:read only.
        var client = UserClient(ChoRolePermissions.ClaimsExaminer);
        using var request = Request(method, path);

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_WithProvidersReadOnly_Returns403()
    {
        var client = UserClient(ChoRolePermissions.ClaimsExaminer);

        var response = await client.PostAsJsonAsync("/api/v1/providers/verify/batch",
            new { npis = new[] { "0000000000" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_WithProvidersWrite_Returns200()
    {
        var client = UserClient(ChoRolePermissions.ProviderRelations);

        var response = await client.PostAsJsonAsync("/api/v1/providers/verify/batch",
            new { npis = new[] { "0000000000" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BatchVerify_ProviderServiceWorkerServiceToken_Returns200()
    {
        // What provider-service's integrity-projection worker sends: a service
        // token minted for the tenant it names in X-Tenant-ID.
        var client = _factory.CreateClient();
        using var request = Request("POST", "/api/v1/providers/verify/batch");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("provider-service", Tenant));
        request.Headers.Add("X-Tenant-ID", Tenant);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void EveryApiEndpoint_NamesAProvidersPermission()
    {
        // Minimal APIs are not reached by the MVC default-permission
        // convention: an endpoint without its own permission would admit any
        // authenticated caller. Every /api endpoint must name one.
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var permissions = endpoint.Metadata.GetOrderedMetadata<RequirePermissionAttribute>()
                .Select(p => p.Permission)
                .ToList();
            Assert.True(permissions.Count > 0, $"{endpoint.RoutePattern.RawText} names no permission");
            Assert.All(permissions, p => Assert.StartsWith("providers:", p));
        }
    }

    // ── External data sources never receive a CHO token ───────────

    [Fact]
    public async Task NppesLookup_InsideAuthenticatedRequest_SendsNoChoToken()
    {
        var client = UserClient(ChoRolePermissions.ProviderRelations);

        await client.GetAsync($"/api/v1/providers/{ValidNpi}/nppes");

        var outbound = Assert.Single(_factory.Nppes.Requests);
        Assert.Equal("npiregistry.cms.hhs.gov", outbound.RequestUri!.Host);
        Assert.Null(outbound.Headers.Authorization);
        Assert.False(outbound.Headers.Contains("X-Tenant-ID"));
    }

    [Fact]
    public async Task NppesLookup_WithInternalLookingBaseUrl_StillSendsNoChoToken()
    {
        // A mirror configured with a dotless host is one the shared handler
        // would treat as a CHO service. External-source clients drop the
        // handler, so the caller's token still never leaves.
        var mirror = new CapturingHandler();
        await using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ProviderVerification:NppesApiBaseUrl", "http://nppes-mirror/api/");
            b.ConfigureServices(s => s.AddHttpClient<INppesAdapter, NppesHttpAdapter>()
                .ConfigurePrimaryHttpMessageHandler(() => mirror));
        });
        var client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(ChoRolePermissions.ProviderRelations));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        await client.GetAsync($"/api/v1/providers/{ValidNpi}/nppes");

        var outbound = Assert.Single(mirror.Requests);
        Assert.Equal("nppes-mirror", outbound.RequestUri!.Host);
        Assert.Null(outbound.Headers.Authorization);
    }

    [Theory]
    [InlineData("https://npiregistry.cms.hhs.gov/api/?version=2.1&number=1234567893")]
    [InlineData("https://oig.hhs.gov/exclusions/downloadables/UPDATED.csv")]
    [InlineData("https://api.sam.gov/entity-information/v3/exclusions")]
    [InlineData("https://data.cms.gov/data-api/v1/dataset")]
    [InlineData("https://openpaymentsdata.cms.gov/api/1/datastore")]
    [InlineData("https://clinicaltables.nlm.nih.gov/api/")]
    public async Task FactoryClient_NoCaller_TenantNamed_ExternalHost_GetsNoToken(string url)
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-ID", Tenant);

        await client.SendAsync(request);

        Assert.Null(Assert.Single(_factory.Probe.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData("http://provider-service/api/v1/providers")]
    [InlineData("http://provider-service.cloudhealthoffice/api/v1/providers")]
    [InlineData("http://tenant-service.cloudhealthoffice.svc.cluster.local/api/v1/tenants")]
    public async Task FactoryClient_NoCaller_TenantNamed_ChoHost_GetsServiceTokenForThatTenant(string url)
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-ID", Tenant);

        await client.SendAsync(request);

        var auth = Assert.Single(_factory.Probe.Requests).Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.Parameter);
        Assert.Equal(Tenant, jwt.Claims.Single(c => c.Type == ChoClaimTypes.TenantId).Value);
        Assert.Equal("provider-verification-service", jwt.Subject);
        Assert.Contains(jwt.Claims, c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
    }

    // ── Probes stay open ──────────────────────────────────────────

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthProbes_NeedNoToken(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
