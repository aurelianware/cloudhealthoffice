using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TenantService.Models;
using static CloudHealthOffice.TenantService.Tests.Security.TenantServiceFactory;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// <c>POST /internal/v1/tenants/{tenantId}/onboarding-complete</c>: the
/// tenant-onboarding workflow's activation step. Only the
/// <c>wf-tenant-onboarding</c> workload identity, only for the tenant its token
/// names, and only pending → active.
/// </summary>
public class OnboardingRouteTests : IClassFixture<TenantServiceFactory>
{
    private const string Route = "/internal/v1/tenants/{0}/onboarding-complete";
    private const string AuditCategory = "CloudHealthOffice.TenantService.Audit";
    private const string Onboarding = "wf-tenant-onboarding";

    private readonly TenantServiceFactory _factory;

    public OnboardingRouteTests(TenantServiceFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
        _factory.Tenants.Setup(r => r.TryActivatePendingAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
    }

    private void TenantStatus(string tenantId, string status)
        => _factory.Tenants.Setup(r => r.GetByTenantIdAsync(tenantId))
            .ReturnsAsync(() => { var t = NewTenant(tenantId); t.Status = status; return t; });

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Workload(string clientId, string tenant, params string[] permissions)
        => ChoDevelopmentAuth.WorkloadTokenIssuer()
            .IssueWorkloadToken(clientId, tenant, permissions.Length > 0 ? permissions : ["settings:manage"]);

    private static Task<HttpResponseMessage> Post(HttpClient client, string tenant)
        => client.PostAsync(string.Format(Route, tenant), null);

    private IEnumerable<string> Audit()
        => _factory.Logs.Entries.Where(e => e.Category == AuditCategory).Select(e => e.Message);

    // ── allowed ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnboardingWorkload_ActivatesItsPendingTenant_Audited()
    {
        TenantStatus(TenantA, "pending");

        var response = await Post(Bearer(Workload(Onboarding, TenantA)), TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["changed"].ToString().Should().Be("True");
        _factory.Tenants.Verify(r => r.TryActivatePendingAsync(TenantA, Onboarding), Times.Once);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        Audit().Should().ContainSingle(m => m.Contains("onboarding-complete") && m.Contains(TenantA)
                                            && m.Contains(Onboarding) && m.Contains("allowed: activated"));
    }

    [Fact]
    public async Task AlreadyActiveTenant_IsLeftAlone()
    {
        TenantStatus(TenantA, "active");

        var response = await Post(Bearer(Workload(Onboarding, TenantA)), TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Tenants.Verify(r => r.TryActivatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Audit().Should().ContainSingle(m => m.Contains("already_active"));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("terminated")]
    public async Task SuspendedOrTerminatedTenant_IsNeverReactivated(string status)
    {
        TenantStatus(TenantA, status);

        var response = await Post(Bearer(Workload(Onboarding, TenantA)), TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Tenants.Verify(r => r.TryActivatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        Audit().Should().ContainSingle(m => m.Contains("refused: tenant_not_pending"));
    }

    [Fact]
    public async Task TenantSuspendedBetweenReadAndWrite_IsNotActivated()
    {
        var reads = 0;
        _factory.Tenants.Setup(r => r.GetByTenantIdAsync(TenantA))
            .ReturnsAsync(() => { var t = NewTenant(TenantA); t.Status = reads++ == 0 ? "pending" : "suspended"; return t; });
        _factory.Tenants.Setup(r => r.TryActivatePendingAsync(TenantA, It.IsAny<string>())).ReturnsAsync(false);

        var response = await Post(Bearer(Workload(Onboarding, TenantA)), TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UnknownTenant_Returns404()
    {
        var response = await Post(Bearer(Workload(Onboarding, "tenant-new")), "tenant-new");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Audit().Should().ContainSingle(m => m.Contains("refused: not_found"));
    }

    // ── refused ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnboardingWorkload_CannotActivateAnotherTenant()
    {
        TenantStatus(TenantA, "pending");

        var response = await Post(Bearer(Workload(Onboarding, TenantB)), TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.TryActivatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    public static TheoryData<string, string> OtherCallers() => new()
    {
        { "another workload", Workload("wf-enrollment-import", TenantA, "enrollment:process") },
        { "service token named like the workflow", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(Onboarding, TenantA) },
        { "token-service", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("token-service", TenantA) },
        { "tenant admin", ChoDevelopmentAuth.UserToken(TenantA, ChoRolePermissions.TenantAdmin) },
        { "platform admin", ChoDevelopmentAuth.UserToken(TenantA, ChoRolePermissions.PlatformAdmin) },
        { "user named like the workflow", ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(Onboarding, TenantA, [ChoRolePermissions.TenantAdmin]) },
        { "user issuer forging the workload marker", Forge(ChoDevelopmentAuth.UserIssuer) },
        { "service issuer forging the workload marker", Forge(ChoDevelopmentAuth.ServiceIssuer) },
    };

    [Theory]
    [MemberData(nameof(OtherCallers))]
    public async Task EveryOtherCaller_IsForbidden(string caller, string token)
    {
        TenantStatus(TenantA, "pending");

        var response = await Post(Bearer(token), TenantA);

        // A service-issuer token without cho.service is refused at
        // authentication (401); every other caller is authenticated but not
        // the onboarding workflow (403).
        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], caller);
        _factory.Tenants.Verify(r => r.TryActivatePendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        (await _factory.CreateClient().PostAsync(string.Format(Route, TenantA), null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── the workload token elsewhere in tenant-service ──────────────────

    [Fact]
    public async Task OnboardingWorkload_CanReadItsTenant_ButNotUsePlatformRoutes()
    {
        var client = Bearer(Workload(Onboarding, TenantA));

        (await client.GetAsync($"/api/v1/tenants/{TenantA}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/v1/tenants/{TenantA}/activate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/v1/tenants/{TenantA}/suspend", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/v1/tenants/{TenantA}", new { status = "active" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/internal/v1/identity/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
    }

    private static string Forge(string issuer)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = ChoDevelopmentAuth.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Onboarding, ["azp"] = Onboarding, ["tenant_id"] = TenantA,
                ["roles"] = new[] { ChoWorkloadRole.Name },
                ["permissions"] = new[] { "settings:manage" },
                ["cho_workload_issuer"] = "true",
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)), SecurityAlgorithms.HmacSha256),
        });
}
