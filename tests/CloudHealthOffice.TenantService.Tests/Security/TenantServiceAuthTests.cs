using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using TenantService.Models;
using static CloudHealthOffice.TenantService.Tests.Security.TenantServiceFactory;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// tenant-service requires CHO tokens. Tenant routes act on the caller's own
/// tenant only (platform administrators excepted, and audited); platform
/// routes need platform:tenants; token-service's identity endpoints admit only
/// the token-service service identity.
/// </summary>
public class TenantServiceAuthTests : IClassFixture<TenantServiceFactory>
{
    private const string AuditCategory = "CloudHealthOffice.TenantService.Audit";
    private readonly TenantServiceFactory _factory;

    public TenantServiceAuthTests(TenantServiceFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
    }

    private HttpClient As(string role, string tenant = TenantA, string subject = "user-7")
        => _factory.UserClient(tenant, subject, role);

    private static object NewUserBody(params string[] roles) => new
    {
        email = "new.user@tenant-a.example",
        displayName = "New User",
        roles = roles.Length > 0 ? roles : new[] { ChoRolePermissions.ClaimsExaminer },
    };

    private static object NewTenantBody => new
    {
        tenantName = "Acme",
        organizationName = "Acme Health",
        subscriptionTier = "starter",
        contactInfo = new { primaryContact = "Pat", email = "pat@acme.example", phone = "555" },
    };

    private static object ConfigBody(bool enforce) => new
    {
        configuration = new { paymentControls = new { enforceSeparationOfDuties = enforce } },
    };

    // ── no token ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/v1/tenants/tenant-a")]
    [InlineData("GET", "/api/v1/tenants")]
    [InlineData("POST", "/api/v1/tenants")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/activate")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/suspend")]
    [InlineData("PUT", "/api/v1/tenants/tenant-a")]
    [InlineData("GET", "/api/v1/tenants/tenant-a/users")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/users")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/api-keys")]
    [InlineData("PUT", "/api/v1/tenants/tenant-a/operating-mode")]
    [InlineData("POST", "/api/v1/roles")]
    [InlineData("GET", "/internal/v1/identity/memberships?tid=t&oid=o")]
    [InlineData("POST", "/internal/v1/identity/tenants/tenant-a/users/user-1/entra-link")]
    public async Task NoToken_Returns401(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET") request.Content = JsonContent.Create(new { });

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Tenants.Verify(r => r.CreateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Users.Verify(r => r.CreateAsync(It.IsAny<TenantUser>()), Times.Never);
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
    }

    // ── route tenant must be the token tenant ───────────────────────────

    [Theory]
    [InlineData("GET", "/api/v1/tenants/tenant-b")]
    [InlineData("PUT", "/api/v1/tenants/tenant-b")]
    [InlineData("GET", "/api/v1/tenants/tenant-b/users")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/users")]
    [InlineData("PUT", "/api/v1/tenants/tenant-b/users/user-1")]
    [InlineData("GET", "/api/v1/tenants/tenant-b/api-keys")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/api-keys")]
    [InlineData("DELETE", "/api/v1/tenants/tenant-b/api-keys/k1")]
    [InlineData("GET", "/api/v1/tenants/tenant-b/operating-mode")]
    [InlineData("PUT", "/api/v1/tenants/tenant-b/operating-mode")]
    [InlineData("GET", "/api/v1/tenants/tenant-b/usage")]
    [InlineData("GET", "/api/v1/billing/tenants/tenant-b/invoices")]
    [InlineData("POST", "/api/v1/billing/tenants/tenant-b/cancel")]
    public async Task RouteTenantOtherThanTokenTenant_Returns403(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(new { name = "k", email = "x@y.example", displayName = "x", roles = new[] { "ClaimsExaminer" }, engines = new { } });

        var response = await As(ChoRolePermissions.TenantAdmin).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.GetByTenantIdAsync(TenantB), Times.Never);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Users.Verify(r => r.GetByTenantIdAsync(It.IsAny<string>()), Times.Never);
        _factory.Users.Verify(r => r.CreateAsync(It.IsAny<TenantUser>()), Times.Never);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnTenant_IsServed()
    {
        var response = await As(ChoRolePermissions.TenantAdmin).GetAsync("/api/v1/tenants/tenant-a/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Users.Verify(r => r.GetByTenantIdAsync(TenantA), Times.Once);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_Returns403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ChoDevelopmentAuth.UserToken(TenantA, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", TenantB);

        (await client.GetAsync("/api/v1/tenants/tenant-b/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Users.Verify(r => r.GetByTenantIdAsync(It.IsAny<string>()), Times.Never);
    }

    // ── platform administrators may cross tenants, and are audited ──────

    [Fact]
    public async Task PlatformAdmin_CrossTenant_IsAllowedAndAudited()
    {
        var response = await As(ChoRolePermissions.PlatformAdmin, TenantA, "platform:cho:ops-1")
            .GetAsync("/api/v1/tenants/tenant-b/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Users.Verify(r => r.GetByTenantIdAsync(TenantB), Times.Once);

        var audit = _factory.Logs.Entries.Where(e => e.Category == AuditCategory).Select(e => e.Message).ToList();
        audit.Should().ContainSingle(m => m.Contains("tenant-b") && m.Contains("platform:cho:ops-1")
                                          && m.Contains("GET") && m.Contains("users"));
    }

    [Fact]
    public async Task PlatformAdmin_CrossTenantWrite_IsAudited()
    {
        var response = await As(ChoRolePermissions.PlatformAdmin, TenantA, "ops-2")
            .PutAsJsonAsync("/api/v1/tenants/tenant-b", ConfigBody(true));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Logs.Entries.Should().Contain(e => e.Category == AuditCategory
                                                    && e.Message.Contains("tenant-b") && e.Message.Contains("ops-2")
                                                    && e.Message.Contains("PUT"));
    }

    // ── permission boundaries ───────────────────────────────────────────

    [Fact]
    public async Task ClaimsExaminer_CannotListOrManageUsers()
    {
        var client = As(ChoRolePermissions.ClaimsExaminer);

        (await client.GetAsync("/api/v1/tenants/tenant-a/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/tenants/tenant-a/users", NewUserBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync("/api/v1/tenants/tenant-a/users/user-1", new { status = "Disabled" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync("/api/v1/tenants/tenant-a/users/user-1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.Users.Verify(r => r.CreateAsync(It.IsAny<TenantUser>()), Times.Never);
        _factory.Users.Verify(r => r.UpdateAsync(It.IsAny<TenantUser>()), Times.Never);
        _factory.Users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TenantAdmin_CanManageUsers()
    {
        var client = As(ChoRolePermissions.TenantAdmin);

        (await client.PostAsJsonAsync("/api/v1/tenants/tenant-a/users", NewUserBody())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PutAsJsonAsync("/api/v1/tenants/tenant-a/users/user-1", new { status = "Disabled" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantAdmin_CannotGrantPlatformAdmin()
    {
        var response = await As(ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/v1/tenants/tenant-a/users", NewUserBody(ChoRolePermissions.PlatformAdmin));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Users.Verify(r => r.CreateAsync(It.IsAny<TenantUser>()), Times.Never);
    }

    [Theory]
    [InlineData("POST", "/api/v1/tenants")]
    [InlineData("GET", "/api/v1/tenants")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/activate")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/suspend")]
    [InlineData("DELETE", "/api/v1/tenants/tenant-a")]
    [InlineData("PUT", "/api/v1/billing/tenants/tenant-a/tier?newTier=enterprise")]
    [InlineData("POST", "/api/v1/roles")]
    [InlineData("POST", "/api/v1/roles/seed")]
    public async Task TenantAdmin_CannotDoPlatformActions(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET" && method != "DELETE")
            request.Content = JsonContent.Create(path.EndsWith("/api/v1/roles") ? new { roleName = "X", permissions = new[] { "claims:read" } } : NewTenantBody);

        var response = await As(ChoRolePermissions.TenantAdmin).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.CreateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Tenants.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
        _factory.Tenants.Verify(r => r.GetAllAsync(It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        _factory.Roles.Verify(r => r.CreateAsync(It.IsAny<TenantRole>()), Times.Never);
        _factory.Roles.Verify(r => r.SeedStandardRolesAsync(), Times.Never);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("active")]
    [InlineData("")]
    public async Task TenantAdmin_CannotChangeOwnStatusOrTier(string status)
    {
        object body = status.Length > 0 ? new { status } : new { subscriptionTier = "enterprise" };

        var response = await As(ChoRolePermissions.TenantAdmin).PutAsJsonAsync("/api/v1/tenants/tenant-a", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
    }

    [Fact]
    public async Task PlatformAdmin_CanCreateTenant_AndItIsAudited()
    {
        var response = await As(ChoRolePermissions.PlatformAdmin, TenantA, "ops-3").PostAsJsonAsync("/api/v1/tenants", NewTenantBody);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Logs.Entries.Should().Contain(e => e.Category == AuditCategory
                                                    && e.Message.Contains("create tenant") && e.Message.Contains("ops-3"));
    }

    [Fact]
    public async Task Settings_NeedSettingsManage()
    {
        (await As(ChoRolePermissions.ClaimsExaminer).PutAsJsonAsync("/api/v1/tenants/tenant-a", ConfigBody(true)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(ChoRolePermissions.Finance).PutAsJsonAsync("/api/v1/tenants/tenant-a", ConfigBody(false)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);

        (await As(ChoRolePermissions.TenantAdmin).PutAsJsonAsync("/api/v1/tenants/tenant-a", ConfigBody(true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.Is<Tenant>(t =>
            t.TenantId == TenantA && t.Configuration.PaymentControls.EnforceSeparationOfDuties)), Times.Once);
    }

    [Fact]
    public async Task OperatingMode_ReadByAnyMember_WrittenWithOperatingModeManage()
    {
        (await As(ChoRolePermissions.ClaimsExaminer).GetAsync("/api/v1/tenants/tenant-a/operating-mode"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var change = new { engines = new Dictionary<string, string> { ["benefitCalculation"] = "augment" } };
        (await As(ChoRolePermissions.Finance).PutAsJsonAsync("/api/v1/tenants/tenant-a/operating-mode", change))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);

        (await As(ChoRolePermissions.TenantAdmin).PutAsJsonAsync("/api/v1/tenants/tenant-a/operating-mode", change))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiKeys_NeedSettingsManage_AndNeverExposeTheHash()
    {
        (await As(ChoRolePermissions.ClaimsExaminer).PostAsJsonAsync("/api/v1/tenants/tenant-a/api-keys", new { name = "k" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(ChoRolePermissions.ClaimsExaminer).GetAsync("/api/v1/tenants/tenant-a/api-keys"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = As(ChoRolePermissions.TenantAdmin);
        var created = await admin.PostAsJsonAsync("/api/v1/tenants/tenant-a/api-keys", new { name = "k" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadAsStringAsync()).Should().Contain("cho_");

        var list = await admin.GetAsync("/api/v1/tenants/tenant-a/api-keys");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync();
        body.Should().Contain("cho_abcd").And.NotContain("HASH-OF-SECRET-KEY").And.NotContain("keyHash");
    }

    [Fact]
    public async Task Roles_ReadByAnyUser()
    {
        (await As(ChoRolePermissions.ClaimsExaminer).GetAsync("/api/v1/roles")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── reading your own tenant ─────────────────────────────────────────

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    public async Task AnyMember_ReadsOwnTenant_IncludingPaymentControls_ButNoSecrets(string role)
    {
        var response = await As(role).GetAsync("/api/v1/tenants/tenant-a");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("configuration").GetProperty("paymentControls")
            .GetProperty("enforceSeparationOfDuties").GetBoolean().Should().BeFalse();
        json.Should().NotContain("cus_SECRET").And.NotContain("sub_SECRET")
            .And.NotContain("HASH-OF-SECRET-KEY").And.NotContain("cho_abcd");
    }

    [Fact]
    public async Task SettingsManager_ReadsBillingAndKeyRecords_ButNotKeyHashes()
    {
        var json = await (await As(ChoRolePermissions.TenantAdmin).GetAsync("/api/v1/tenants/tenant-a")).Content.ReadAsStringAsync();

        json.Should().Contain("cus_SECRET").And.Contain("cho_abcd").And.NotContain("HASH-OF-SECRET-KEY");
    }

    // ── service tokens ──────────────────────────────────────────────────

    [Fact]
    public async Task ServiceToken_ReadsItsOwnTenantConfiguration()
    {
        var response = await _factory.ServiceClient("claims-service", TenantA).GetAsync("/api/v1/tenants/tenant-a");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.ServiceClient("benefit-plan-service", TenantA).GetAsync("/api/v1/tenants/tenant-a/operating-mode"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("POST", "/api/v1/tenants")]
    [InlineData("GET", "/api/v1/tenants")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/activate")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/suspend")]
    [InlineData("DELETE", "/api/v1/tenants/tenant-a")]
    [InlineData("GET", "/api/v1/tenants/tenant-b")]
    [InlineData("GET", "/api/v1/tenants/tenant-b/users")]
    [InlineData("POST", "/api/v1/roles/seed")]
    public async Task GenericServiceToken_CannotDoPlatformOrCrossTenantActions(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(NewTenantBody);

        var response = await _factory.ServiceClient("claims-service", TenantA).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Tenants.Verify(r => r.CreateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Tenants.Verify(r => r.UpdateAsync(It.IsAny<Tenant>()), Times.Never);
        _factory.Tenants.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
        _factory.Tenants.Verify(r => r.GetByTenantIdAsync(TenantB), Times.Never);
        _factory.Roles.Verify(r => r.SeedStandardRolesAsync(), Times.Never);
    }

    [Fact]
    public async Task TokenServiceIdentity_CannotDoPlatformActionsEither()
    {
        var response = await _factory.ServiceClient("token-service", "cho-platform").GetAsync("/api/v1/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── token-service identity endpoints ────────────────────────────────

    [Theory]
    [InlineData("cho-platform", "/internal/v1/identity/memberships?tid=tid-1&oid=oid-1")]
    [InlineData("cho-platform", "/internal/v1/identity/tenants")]
    [InlineData("cho-platform", "/internal/v1/identity/tenants/tenant-b")]
    [InlineData("tenant-b", "/internal/v1/identity/tenants/tenant-b")]
    public async Task IdentityEndpoints_AllowTokenService(string tokenTenant, string path)
    {
        var response = await _factory.ServiceClient("token-service", tokenTenant).GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IdentityLink_AllowsTokenService()
    {
        _factory.Directory.Setup(d => d.LinkEntraIdentityAsync(TenantB, "user-9", "oid-1", "tid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::TenantService.Services.LinkOutcome.Linked, new global::TenantService.Services.IdentityUser { Id = "user-9" }));

        var response = await _factory.ServiceClient("token-service", TenantB)
            .PostAsJsonAsync("/internal/v1/identity/tenants/tenant-b/users/user-9/entra-link",
                new { azureAdObjectId = "oid-1", azureAdTenantId = "tid-1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> IdentityPaths => new()
    {
        "/internal/v1/identity/memberships?tid=tid-1&oid=oid-1",
        "/internal/v1/identity/tenants",
        "/internal/v1/identity/tenants/tenant-a",
    };

    [Theory]
    [MemberData(nameof(IdentityPaths))]
    public async Task IdentityEndpoints_RefuseOtherServices(string path)
    {
        (await _factory.ServiceClient("claims-service", TenantA).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.ServiceClient("portal", "cho-platform").GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Directory.Invocations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(IdentityPaths))]
    public async Task IdentityEndpoints_RefuseUserTokens(string path)
    {
        (await As(ChoRolePermissions.PlatformAdmin).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(ChoRolePermissions.TenantAdmin, TenantA, "token-service").GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Directory.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task IdentityLink_RefusesUsersAndOtherServices()
    {
        var body = new { azureAdObjectId = "attacker-oid", azureAdTenantId = "attacker-tid" };

        (await As(ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/internal/v1/identity/tenants/tenant-a/users/user-1/entra-link", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.ServiceClient("claims-service", TenantA).PostAsJsonAsync("/internal/v1/identity/tenants/tenant-a/users/user-1/entra-link", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Directory.Invocations.Should().BeEmpty();
    }

    // ── the actor comes from the token ──────────────────────────────────

    [Fact]
    public async Task UserCreate_RecordsTokenActor_IgnoringBody()
    {
        TenantUser? saved = null;
        _factory.Users.Setup(r => r.CreateAsync(It.IsAny<TenantUser>()))
            .Callback<TenantUser>(u => saved = u).ReturnsAsync((TenantUser u) => u);

        var response = await As(ChoRolePermissions.TenantAdmin, TenantA, "admin-42").PostAsJsonAsync(
            "/api/v1/tenants/tenant-a/users", new
            {
                email = "new.user@tenant-a.example",
                displayName = "New User",
                roles = new[] { ChoRolePermissions.ClaimsExaminer },
                tenantId = TenantB,
                createdBy = "attacker",
                updatedBy = "attacker",
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        saved!.CreatedBy.Should().Be("admin-42");
        saved.UpdatedBy.Should().Be("admin-42");
        saved.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public async Task UserUpdate_RecordsTokenActor_IgnoringBody()
    {
        TenantUser? saved = null;
        _factory.Users.Setup(r => r.UpdateAsync(It.IsAny<TenantUser>()))
            .Callback<TenantUser>(u => saved = u).ReturnsAsync((TenantUser u) => u);

        var response = await As(ChoRolePermissions.TenantAdmin, TenantA, "admin-42").PutAsJsonAsync(
            "/api/v1/tenants/tenant-a/users/user-1", new { status = "Disabled", updatedBy = "attacker" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        saved!.UpdatedBy.Should().Be("admin-42");
    }

    [Fact]
    public async Task TenantCreateAndApiKey_RecordTokenActor_IgnoringBody()
    {
        Tenant? created = null;
        _factory.Tenants.Setup(r => r.CreateAsync(It.IsAny<Tenant>()))
            .Callback<Tenant>(t => created = t).ReturnsAsync((Tenant t) => t);
        var platform = As(ChoRolePermissions.PlatformAdmin, TenantA, "ops-9");

        var tenantBody = new
        {
            tenantName = "Acme",
            organizationName = "Acme Health",
            subscriptionTier = "starter",
            contactInfo = new { primaryContact = "Pat", email = "pat@acme.example", phone = "555" },
            createdBy = "attacker",
        };
        (await platform.PostAsJsonAsync("/api/v1/tenants", tenantBody)).StatusCode.Should().Be(HttpStatusCode.Created);
        created!.CreatedBy.Should().Be("ops-9");

        Tenant? updated = null;
        _factory.Tenants.Setup(r => r.UpdateAsync(It.IsAny<Tenant>()))
            .Callback<Tenant>(t => updated = t).ReturnsAsync((Tenant t) => t);
        (await As(ChoRolePermissions.TenantAdmin, TenantA, "admin-42")
                .PostAsJsonAsync("/api/v1/tenants/tenant-a/api-keys", new { name = "k", createdBy = "attacker" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        updated!.ApiKeys.Last().CreatedBy.Should().Be("admin-42");
    }

    [Fact]
    public async Task RoleCreate_RecordsTokenActor_IgnoringBody()
    {
        TenantRole? saved = null;
        _factory.Roles.Setup(r => r.CreateAsync(It.IsAny<TenantRole>()))
            .Callback<TenantRole>(r => saved = r).ReturnsAsync((TenantRole r) => r);

        var response = await As(ChoRolePermissions.PlatformAdmin, TenantA, "ops-9").PostAsJsonAsync("/api/v1/roles",
            new { roleName = "Auditor", permissions = new[] { "audit:read" }, createdBy = "attacker" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        saved!.CreatedBy.Should().Be("ops-9");
    }
}
