using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using TenantService.Models;
using TenantService.Services;
using static CloudHealthOffice.TenantService.Tests.Security.TenantServiceFactory;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// Invitation routes, the unlink route, the removed admin-set link and the
/// internal redemption endpoint, through the real pipeline with a mocked store.
/// </summary>
public class InvitationEndpointTests : IClassFixture<TenantServiceFactory>
{
    private const string AuditCategory = "CloudHealthOffice.TenantService.Audit";
    private readonly TenantServiceFactory _factory;
    private Invitation? _created;

    public InvitationEndpointTests(TenantServiceFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
        _factory.Invitations
            .Setup(s => s.CreateAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Invitation i, CancellationToken _) =>
            {
                _created = i;
                i.UserId = "user-new";
                return new InvitationResult(InvitationError.None, i);
            });
    }

    private HttpClient As(string role, string tenant = TenantA, string subject = "admin-7")
        => _factory.UserClient(tenant, subject, role);

    private HttpClient TokenService() => _factory.ServiceClient(InternalIdentityControllerClientId, "cho-platform");

    private const string InternalIdentityControllerClientId = "token-service";

    private static object InviteBody(params string[] roles) => new
    {
        email = " Pat.Guest@Partner.example ",
        firstName = "Pat",
        lastName = "Guest",
        department = "Claims",
        roles = roles.Length > 0 ? roles : new[] { ChoRolePermissions.ClaimsExaminer },
        // Ignored: the creator comes from the token.
        createdBy = "someone-else",
        codeHash = "attacker-chosen",
    };

    private static Invitation Stored(string id = "inv-1", string status = InvitationStatus.Pending) => new()
    {
        Id = id,
        TenantId = TenantA,
        UserId = "user-new",
        Email = "pat@partner.example",
        EmailNormalized = "pat@partner.example",
        Roles = new List<string> { "ClaimsExaminer" },
        CodeHash = "STORED-CODE-HASH-VALUE",
        Status = status,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
    };

    // ── permissions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/v1/tenants/tenant-a/invitations")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/invitations")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/invitations/inv-1/revoke")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/invitations/inv-1/resend")]
    [InlineData("POST", "/api/v1/tenants/tenant-a/users/user-1/unlink")]
    public async Task WithoutUsersManage_Returns403(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(InviteBody());

        var response = await As(ChoRolePermissions.ClaimsExaminer).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Invitations.Invocations.Should().BeEmpty();
        _factory.Users.Verify(r => r.UnlinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tenants/tenant-b/invitations")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/invitations")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/invitations/inv-1/revoke")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/invitations/inv-1/resend")]
    [InlineData("POST", "/api/v1/tenants/tenant-b/users/user-1/unlink")]
    public async Task OtherTenantsRoutes_Return403(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(InviteBody());

        var response = await As(ChoRolePermissions.TenantAdmin).SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Invitations.Invocations.Should().BeEmpty();
    }

    // ── create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ReturnsTheCodeAndLinkOnce_AndStoresOnlyTheCodesHash()
    {
        var response = await As(ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/v1/tenants/tenant-a/invitations", InviteBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        var code = body.GetProperty("code").GetString()!;
        code.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        body.GetProperty("redemptionUrl").GetString().Should().EndWith("/invite/" + code);
        body.GetProperty("invitation").GetProperty("status").GetString().Should().Be("Pending");
        body.GetProperty("invitation").GetProperty("userId").GetString().Should().Be("user-new");

        _created.Should().NotBeNull();
        _created!.CodeHash.Should().Be(InvitationCodes.Hash(code)).And.NotBe(code);
        _created.TenantId.Should().Be(TenantA);
        _created.Email.Should().Be("Pat.Guest@Partner.example");
        _created.EmailNormalized.Should().Be("pat.guest@partner.example");
        _created.CreatedBy.Should().Be("admin-7", "the creator comes from the token, not the body");
        _created.DisplayName.Should().Be("Pat Guest");
        _created.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
        text.Should().NotContain(_created.CodeHash).And.NotContainEquivalentOf("codeHash");
        _factory.Logs.Entries.Should().NotContain(e => e.Message.Contains(code) || e.Message.Contains("pat.guest", StringComparison.OrdinalIgnoreCase));
        _factory.Logs.Entries.Should().Contain(e => e.Category == AuditCategory && e.Message.Contains("invitation created"));
    }

    [Theory]
    [InlineData(ChoRolePermissions.PlatformAdmin)]
    [InlineData(ChoServiceRole.Name)]
    public async Task Create_GrantingAPlatformOnlyRole_IsRefused(string role)
    {
        var response = await As(ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/v1/tenants/tenant-a/invitations", InviteBody(ChoRolePermissions.ClaimsExaminer, role));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Invitations.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_UnknownRole_Returns400()
    {
        var response = await As(ChoRolePermissions.TenantAdmin)
            .PostAsJsonAsync("/api/v1/tenants/tenant-a/invitations", InviteBody("NoSuchRole"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Invitations.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(InvitationError.UserExists, "user_exists")]
    [InlineData(InvitationError.InvitationPending, "invitation_pending")]
    public async Task Create_ForAnAddressAlreadyInTheTenant_Returns409(InvitationError error, string code)
    {
        _factory.Invitations
            .Setup(s => s.CreateAsync(It.IsAny<Invitation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationResult(error));

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/v1/tenants/tenant-a/invitations", InviteBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be(code);
    }

    // ── list, revoke, resend ────────────────────────────────────────────

    [Fact]
    public async Task List_NeverReturnsHashes()
    {
        _factory.Invitations.Setup(s => s.ListAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Stored("inv-1"), Stored("inv-2", InvitationStatus.Redeemed) });

        var response = await As(ChoRolePermissions.TenantAdmin).GetAsync("/api/v1/tenants/tenant-a/invitations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("STORED-CODE-HASH-VALUE").And.NotContainEquivalentOf("codeHash");
        JsonDocument.Parse(text).RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task List_ShowsAPendingInvitationPastItsExpiryAsExpired()
    {
        var expired = Stored();
        expired.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _factory.Invitations.Setup(s => s.ListAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { expired });

        var body = await As(ChoRolePermissions.TenantAdmin).GetFromJsonAsync<JsonElement>("/api/v1/tenants/tenant-a/invitations");

        body[0].GetProperty("status").GetString().Should().Be("Expired");
    }

    [Theory]
    [InlineData(InvitationError.None, HttpStatusCode.OK)]
    [InlineData(InvitationError.NotFound, HttpStatusCode.NotFound)]
    [InlineData(InvitationError.AlreadyRedeemed, HttpStatusCode.Conflict)]
    public async Task Revoke_IsRecordedWithTheTokenSubject(InvitationError error, HttpStatusCode expected)
    {
        _factory.Invitations.Setup(s => s.RevokeAsync(TenantA, "inv-1", "admin-7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationResult(error, error == InvitationError.NotFound ? null : Stored(status: InvitationStatus.Revoked)));

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsync("/api/v1/tenants/tenant-a/invitations/inv-1/revoke", null);

        response.StatusCode.Should().Be(expected);
        _factory.Invitations.Verify(s => s.RevokeAsync(TenantA, "inv-1", "admin-7", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resend_IssuesANewCode_WhoseHashIsWhatIsStored()
    {
        string? storedHash = null;
        _factory.Invitations
            .Setup(s => s.ResendAsync(TenantA, "inv-1", It.IsAny<string>(), It.IsAny<DateTime>(), "admin-7", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string hash, DateTime _, string _, CancellationToken _) =>
            {
                storedHash = hash;
                return new InvitationResult(InvitationError.None, Stored());
            });

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsync("/api/v1/tenants/tenant-a/invitations/inv-1/resend", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var code = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        storedHash.Should().Be(InvitationCodes.Hash(code));
    }

    // ── users: no admin-set link; unlink ────────────────────────────────

    [Fact]
    public async Task CreateUser_BodyCannotSetTheEntraLink()
    {
        TenantUser? created = null;
        _factory.Users.Setup(r => r.CreateAsync(It.IsAny<TenantUser>()))
            .ReturnsAsync((TenantUser u) => { created = u; return u; });

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/v1/tenants/tenant-a/users", new
        {
            email = "new@tenant-a.example",
            displayName = "New",
            roles = new[] { ChoRolePermissions.ClaimsExaminer },
            azureAdObjectId = "oid-chosen-by-admin",
            azureAdTenantId = "33333333-3333-3333-3333-333333333333",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        created!.AzureAdObjectId.Should().BeEmpty();
        created.AzureAdTenantId.Should().BeEmpty();
        created.Status.Should().Be(TenantUserStatus.Active);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task UpdateUser_BodyCannotSetTheEntraLink(string method)
    {
        TenantUser? updated = null;
        _factory.Users.Setup(r => r.UpdateAsync(It.IsAny<TenantUser>()))
            .ReturnsAsync((TenantUser u) => { updated = u; return u; });

        var response = await As(ChoRolePermissions.TenantAdmin).SendAsync(
            new HttpRequestMessage(new HttpMethod(method), "/api/v1/tenants/tenant-a/users/user-1")
            {
                Content = JsonContent.Create(new
                {
                    displayName = "Renamed",
                    azureAdObjectId = "oid-chosen-by-admin",
                    azureAdTenantId = "33333333-3333-3333-3333-333333333333",
                }),
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        updated!.DisplayName.Should().Be("Renamed");
        updated.AzureAdObjectId.Should().BeEmpty();
        updated.AzureAdTenantId.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateUser_CannotActivateAnInvitedUser()
    {
        _factory.Users.Setup(r => r.GetByIdAsync("user-inv"))
            .ReturnsAsync(new TenantUser { Id = "user-inv", TenantId = TenantA, Email = "g@x.example", Status = TenantUserStatus.Invited });

        var response = await As(ChoRolePermissions.TenantAdmin)
            .PutAsJsonAsync("/api/v1/tenants/tenant-a/users/user-inv", new { status = "Active" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Users.Verify(r => r.UpdateAsync(It.IsAny<TenantUser>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_ChangedConcurrently_Returns409()
    {
        _factory.Users.Setup(r => r.UpdateAsync(It.IsAny<TenantUser>()))
            .ThrowsAsync(new UserChangedConcurrentlyException("user-1"));

        var response = await As(ChoRolePermissions.TenantAdmin)
            .PutAsJsonAsync("/api/v1/tenants/tenant-a/users/user-1", new { displayName = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unlink_ClearsTheLink_AndIsAudited()
    {
        var linked = NewUser("user-1", TenantA);
        linked.AzureAdObjectId = "oid-old";
        linked.AzureAdTenantId = "33333333-3333-3333-3333-333333333333";
        _factory.Users.Setup(r => r.GetByIdAsync("user-1")).ReturnsAsync(linked);
        _factory.Users.Setup(r => r.UnlinkAsync(TenantA, "user-1", "admin-7"))
            .ReturnsAsync(NewUser("user-1", TenantA));

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsync("/api/v1/tenants/tenant-a/users/user-1/unlink", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Users.Verify(r => r.UnlinkAsync(TenantA, "user-1", "admin-7"), Times.Once);
        _factory.Logs.Entries.Should().Contain(e => e.Category == AuditCategory
            && e.Message.Contains("unlink user user-1") && e.Message.Contains("oid-old") && e.Message.Contains("admin-7"));
    }

    [Fact]
    public async Task Unlink_UserOfAnotherTenant_Returns404()
    {
        _factory.Users.Setup(r => r.GetByIdAsync("user-b")).ReturnsAsync(NewUser("user-b", TenantB));

        var response = await As(ChoRolePermissions.TenantAdmin).PostAsync("/api/v1/tenants/tenant-a/users/user-b/unlink", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Users.Verify(r => r.UnlinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── internal redemption ─────────────────────────────────────────────

    private static object RedeemBody(string code) => new
    {
        code,
        tid = "33333333-3333-3333-3333-333333333333",
        oid = "oid-guest",
        email = "pat@partner.example",
    };

    [Fact]
    public async Task InternalRedeem_RefusesUsersAndOtherServices()
    {
        var code = InvitationCodes.NewCode();

        (await As(ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(code)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(ChoRolePermissions.PlatformAdmin).PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(code)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.ServiceClient("claims-service", TenantA).PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(code)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(code)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _factory.Invitations.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("not a code at all, but forty-three chars!!")]
    public async Task InternalRedeem_MalformedCode_IsNotFoundWithoutALookup(string code)
    {
        var response = await TokenService().PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(code));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("not_found");
        _factory.Invitations.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(InvitationError.None, HttpStatusCode.OK, null)]
    [InlineData(InvitationError.NotFound, HttpStatusCode.NotFound, "not_found")]
    [InlineData(InvitationError.Expired, HttpStatusCode.Gone, "expired")]
    [InlineData(InvitationError.Revoked, HttpStatusCode.Gone, "revoked")]
    [InlineData(InvitationError.AlreadyRedeemed, HttpStatusCode.Conflict, "already_redeemed")]
    [InlineData(InvitationError.IdentityInUse, HttpStatusCode.Conflict, "identity_in_use")]
    [InlineData(InvitationError.EmailMismatch, HttpStatusCode.Forbidden, "email_mismatch")]
    public async Task InternalRedeem_PassesTheHashAndIdentity_AndMapsTheOutcome(InvitationError error, HttpStatusCode status, string? code)
    {
        var invitationCode = InvitationCodes.NewCode();
        _factory.Invitations
            .Setup(s => s.RedeemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(error == InvitationError.None
                ? new RedeemResult(InvitationError.None, TenantA, "user-new")
                : new RedeemResult(error, MaskedEmail: error == InvitationError.EmailMismatch ? "p***@partner.example" : null));

        var response = await TokenService().PostAsJsonAsync("/internal/v1/identity/invitations/redeem", RedeemBody(invitationCode));

        response.StatusCode.Should().Be(status);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (code == null)
            body.GetProperty("tenantId").GetString().Should().Be(TenantA);
        else
            body.GetProperty("error").GetString().Should().Be(code);
        if (error == InvitationError.EmailMismatch)
            body.GetProperty("invitedEmail").GetString().Should().Be("p***@partner.example");

        _factory.Invitations.Verify(s => s.RedeemAsync(InvitationCodes.Hash(invitationCode),
            "33333333-3333-3333-3333-333333333333", "oid-guest", "pat@partner.example", It.IsAny<CancellationToken>()), Times.Once);
        _factory.Logs.Entries.Should().NotContain(e => e.Message.Contains(invitationCode));
    }
}
