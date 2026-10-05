using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FfsService.Tests.Security;

/// <summary>
/// ffs-service has no controllers yet (FfsRateConfig is a schema stub), so these
/// tests run the real ffs-service pipeline with one probe controller added. The
/// probe carries no [RequirePermission], exactly like a controller someone adds
/// to the service later: it must get the service's defaults (payments:read /
/// payments:run), and the tenant and actor must come from the token.
///
/// Before this change the pipeline had no authentication at all: every probe
/// call below succeeded anonymously and no tenant was resolved.
/// </summary>
public class FfsPipelineAuthTests : IClassFixture<FfsPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
                services.AddControllers().AddApplicationPart(typeof(FfsProbeController).Assembly));
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "ffs-user-7";
    private const string Probe = "/api/v1/ffs-probe";
    private readonly Factory _factory;

    public FfsPipelineAuthTests(Factory factory) => _factory = factory;

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<ProbeResult> Read(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ProbeResult>())!;

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_WriteIsRejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(Probe, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsRejected()
    {
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(User, Tenant, [ChoRolePermissions.Finance]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAndActor_ComeFromToken_NotQuery()
    {
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(User, Tenant, [ChoRolePermissions.Finance]));

        var response = await client.GetAsync(Probe + "?tenantId=" + OtherTenant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(Tenant, result.ContextTenantId);
        Assert.Equal(User, result.UserId);
    }

    [Fact]
    public async Task WriteActor_IsTokenSubject()
    {
        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync(Probe, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);
        Assert.Equal(User, result.UserId);
        Assert.Equal(Tenant, result.TenantId);
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.FinanceApprover, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.ComplianceOfficer, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.TenantAdmin, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.ClaimsExaminer, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.ProviderRelations, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.MemberServices, HttpStatusCode.Forbidden)]
    public async Task Reads_NeedPaymentsRead(string role, HttpStatusCode expected)
    {
        var response = await Client(Tenant, role).GetAsync(Probe);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.TenantAdmin, HttpStatusCode.OK)]
    [InlineData(ChoRolePermissions.FinanceApprover, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.ComplianceOfficer, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.ClaimsExaminer, HttpStatusCode.Forbidden)]
    [InlineData(ChoRolePermissions.ProviderRelations, HttpStatusCode.Forbidden)]
    public async Task Writes_NeedPaymentsRun(string role, HttpStatusCode expected)
    {
        var response = await Client(Tenant, role).PostAsJsonAsync(Probe, new { });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ServiceToken_CanReadAndWrite_InItsTenant()
    {
        // provider-contracts-service is the only planned caller (sync-children).
        var client = BearerClient(ChoDevelopmentAuth.ServiceTokenIssuer()
            .IssueServiceToken("provider-contracts-service", Tenant));

        var read = await client.GetAsync(Probe);
        var write = await client.PostAsJsonAsync(Probe, new { });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        var result = await Read(write);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal("provider-contracts-service", result.UserId);
        Assert.True(result.IsService);
    }

    [Fact]
    public async Task ServiceRoleInUserIssuerToken_IsNotAService()
    {
        // A user-issuer token cannot claim cho.service: IssueUserToken strips the
        // role, and the service marker is only added for AllowServiceRole issuers.
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(User, Tenant, ["cho.service"], permissions: []));

        var response = await client.GetAsync(Probe);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_NeedNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void DeployedHost_WithoutTrustedIssuers_RefusesToStart()
    {
        // Before: the service started with no authentication configured.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var error = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("ChoAuth:Issuers is empty", error.Message);
    }

    [Fact]
    public void DeployedHost_WithDevelopmentSymmetricKey_RefusesToStart()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                // UseSetting, not ConfigureAppConfiguration: Program reads ChoAuth
                // while registering services, before late configuration sources apply.
                foreach (var (key, value) in ChoDevelopmentAuth.Configuration("ffs-service"))
                    b.UseSetting(key, value);
            });

        var error = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("symmetric key", error.Message);
    }

    public sealed record ProbeResult(string? TenantId, string? ContextTenantId, string? UserId, bool IsService);
}

/// <summary>
/// Stand-in for a future ffs-service controller: no [RequirePermission], so the
/// service defaults apply. Resolves ICurrentActor optionally so the unauthenticated
/// pipeline from before this change can still run it.
/// </summary>
[ApiController]
[Route("api/v1/ffs-probe")]
public sealed class FfsProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(Describe());

    [HttpPost]
    public IActionResult Post() => Ok(Describe());

    private FfsPipelineAuthTests.ProbeResult Describe()
    {
        var actor = HttpContext.RequestServices.GetService<ICurrentActor>();
        var authenticated = actor?.IsAuthenticated == true;
        return new FfsPipelineAuthTests.ProbeResult(
            authenticated ? actor!.TenantId : null,
            HttpContext.Items.TryGetValue("TenantId", out var t) ? t as string : null,
            authenticated ? actor!.UserId : null,
            authenticated && actor!.IsService);
    }
}
