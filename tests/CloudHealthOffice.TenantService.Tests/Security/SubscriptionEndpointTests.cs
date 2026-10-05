using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using TenantService.Security;
using TenantService.Services;
using static CloudHealthOffice.TenantService.Tests.Security.TenantServiceFactory;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// Subscription records are written by tenant-service only: the portal's
/// PlatformTenants page through the platform endpoints (platform:tenants) and
/// self-service signup through token-service's internal signup call, which can
/// set only the organization, a self-service tier and Stripe ids; the
/// directory comes from the validated Entra token and the status is Trial.
/// </summary>
public class SubscriptionEndpointTests : IClassFixture<TenantServiceFactory>
{
    private const string Directory = "11111111-2222-3333-4444-555555555555";
    private readonly TenantServiceFactory _factory;

    public SubscriptionEndpointTests(TenantServiceFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
        _factory.Subscriptions.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SubscriptionRecord> { new() { TenantId = "tenant-x", AzureTenantId = Directory } });
        _factory.Subscriptions.Setup(s => s.CreateAsync(It.IsAny<SubscriptionWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubscriptionWrite w, CancellationToken _) => new SubscriptionRecord
            {
                TenantId = "tenant-new", AzureTenantId = w.AzureTenantId!, SubscriptionStatus = w.SubscriptionStatus ?? "Trial", Tier = w.Tier ?? "starter",
            });
        _factory.Subscriptions.Setup(s => s.UpdateAsync(Directory, It.IsAny<SubscriptionWrite>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _factory.Subscriptions.Setup(s => s.SetStatusAsync(Directory, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _factory.Subscriptions.Setup(s => s.DeleteAsync(Directory, It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private HttpClient As(string role) => _factory.UserClient(TenantA, "admin-7", role);
    private HttpClient TokenService() => _factory.ServiceClient("token-service", "cho-platform");

    public static TheoryData<string, string> PlatformRoutes => new()
    {
        { "GET", "/api/v1/platform/subscriptions" },
        { "POST", "/api/v1/platform/subscriptions" },
        { "PUT", $"/api/v1/platform/subscriptions/{Directory}" },
        { "PUT", $"/api/v1/platform/subscriptions/{Directory}/status" },
        { "DELETE", $"/api/v1/platform/subscriptions/{Directory}" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = path.EndsWith("/status")
                ? JsonContent.Create(new { status = "Active" })
                : JsonContent.Create(new { azureTenantId = Directory, organizationName = "Acme", subscriptionStatus = "Active", tier = "enterprise" });
        }
        return request;
    }

    [Theory]
    [MemberData(nameof(PlatformRoutes))]
    public async Task PlatformRoutes_TenantAdmin_Is403(string method, string path)
    {
        var response = await As(ChoRolePermissions.TenantAdmin).SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Subscriptions.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(PlatformRoutes))]
    public async Task PlatformRoutes_ServiceToken_Is403(string method, string path)
    {
        var request = Request(method, path);
        var response = await _factory.ServiceClient("claims-service", TenantA).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Subscriptions.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(PlatformRoutes))]
    public async Task PlatformRoutes_PlatformAdmin_Succeed_AndAreAudited(string method, string path)
    {
        var response = await As(ChoRolePermissions.PlatformAdmin).SendAsync(Request(method, path));

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        _factory.Subscriptions.Invocations.Should().ContainSingle();
        _factory.Logs.Entries.Should().Contain(e => e.Category == TenantAuditLog.Category && e.Message.Contains("subscription"));
    }

    [Fact]
    public async Task PlatformCreate_InvalidStatus_Is400()
    {
        var response = await As(ChoRolePermissions.PlatformAdmin).PostAsJsonAsync("/api/v1/platform/subscriptions",
            new { azureTenantId = Directory, organizationName = "Acme", subscriptionStatus = "Free-Forever" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Subscriptions.Invocations.Should().BeEmpty();
    }

    // ── signup (token-service only) ───────────────────────────────────────

    private static object SignupBody(string tier = "professional") => new
    {
        tid = Directory,
        oid = "oid-1",
        email = " Owner@Acme.Example ",
        organizationName = " Acme Health ",
        tier,
        stripeCustomerId = "cus_ABC12345",
        stripeSubscriptionId = "sub_ABC12345",
        // Never read: a signup cannot set these.
        subscriptionStatus = "Active",
        isDemo = true,
        azureTenantId = "someone-elses-directory",
        adminEmails = new[] { "attacker@evil.example" },
        notes = "free forever",
    };

    [Fact]
    public async Task Signup_FromTokenService_CreatesATrialForTheTokensDirectory_Only()
    {
        _factory.Subscriptions.Setup(s => s.SignupAsync(It.IsAny<SignupWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionRecord { TenantId = "tenant-new", AzureTenantId = Directory, Tier = "professional" });

        var response = await TokenService().PostAsJsonAsync("/internal/v1/identity/signups", SignupBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["tenantId"].Should().Be("tenant-new");
        _factory.Subscriptions.Verify(s => s.SignupAsync(It.Is<SignupWrite>(w =>
            w.AzureTenantId == Directory
            && w.AdminEmail == "owner@acme.example"
            && w.OrganizationName == "Acme Health"
            && w.Tier == "professional"
            && w.StripeCustomerId == "cus_ABC12345"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Signup_ExistingSubscription_Is409()
    {
        _factory.Subscriptions.Setup(s => s.SignupAsync(It.IsAny<SignupWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubscriptionRecord?)null);

        var response = await TokenService().PostAsJsonAsync("/internal/v1/identity/signups", SignupBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("enterprise")]
    [InlineData("platinum")]
    public async Task Signup_NonSelfServiceTier_Is400(string tier)
    {
        var response = await TokenService().PostAsJsonAsync("/internal/v1/identity/signups", SignupBody(tier));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Subscriptions.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Signup_FromAnyoneButTokenService_Is403()
    {
        var platformAdmin = await As(ChoRolePermissions.PlatformAdmin).PostAsJsonAsync("/internal/v1/identity/signups", SignupBody());
        var otherService = await _factory.ServiceClient("claims-service", "cho-platform").PostAsJsonAsync("/internal/v1/identity/signups", SignupBody());
        var anonymous = await _factory.CreateClient().PostAsJsonAsync("/internal/v1/identity/signups", SignupBody());

        platformAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        otherService.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Subscriptions.Invocations.Should().BeEmpty();
    }
}
