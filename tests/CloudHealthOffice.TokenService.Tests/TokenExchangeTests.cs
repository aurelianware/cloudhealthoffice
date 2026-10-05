using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

public sealed class TokenExchangeTests : IDisposable
{
    private readonly TokenServiceFactory _factory = new();
    private FakeTenantService Directory => _factory.TenantService;

    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";

    public TokenExchangeTests()
    {
        Directory.AddTenant("acme", Ids.AcmeDirectory);
        Directory.AddTenant("beta", Ids.BetaDirectory);
        Directory.AddTenant("cho", Ids.PlatformDirectory);
        Directory.AddTenant("dormant", Ids.AcmeDirectory, active: false);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient As(string token) => _factory.ClientWith(token);

    private static string AcmeUserToken(string oid = Oid, string? username = "pat@acme.example")
        => TestEntra.Token(Ids.AcmeDirectory, oid, username: username);

    // ── Valid exchange ──────────────────────────────────────────────────

    [Fact]
    public async Task Linked_user_gets_a_cho_token_for_the_tenant()
    {
        var user = Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "ClaimsExaminer");

        var response = await As(AcmeUserToken()).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.JsonAsync();
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("expires_in").GetInt32().Should().Be(300);
        body.GetProperty("tenant_id").GetString().Should().Be("acme");
        body.GetProperty("tenant_name").GetString().Should().Be("ACME Health");
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("ClaimsExaminer");
        body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().BeEquivalentTo(ChoRolePermissions.Expand(["ClaimsExaminer"]));
        var u = body.GetProperty("user");
        u.GetProperty("id").GetString().Should().Be(user.Id);
        u.GetProperty("email").GetString().Should().Be("pat@acme.example");
        u.GetProperty("displayName").GetString().Should().Be("Pat Example");
        u.GetProperty("firstName").GetString().Should().Be("Pat");
        u.GetProperty("lastName").GetString().Should().Be("Example");
        u.GetProperty("department").GetString().Should().Be("Claims");

        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Issuer.Should().Be("cho-token-service");
        jwt.Subject.Should().Be(user.Id);
        jwt.GetClaim("tenant_id").Value.Should().Be("acme");
        (jwt.ValidTo - jwt.IssuedAt).Should().Be(TimeSpan.FromMinutes(5));
        Directory.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task Calls_to_tenant_service_carry_a_service_token_never_the_entra_token()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "ClaimsExaminer");
        var entra = AcmeUserToken();

        (await As(entra).ExchangeAsync("acme")).StatusCode.Should().Be(HttpStatusCode.OK);

        Directory.AuthorizationHeaders.Should().NotBeEmpty();
        Directory.AuthorizationHeaders.Should().AllSatisfy(h =>
        {
            h.Should().StartWith("Bearer ");
            h.Should().NotContain(entra);
            var token = new JsonWebToken(h![7..]);
            token.Subject.Should().Be("token-service");
            token.GetClaim("roles").Value.Should().Be(ChoServiceRole.Name);
        });
    }

    // ── Entra token validation → 401 ────────────────────────────────────

    public static TheoryData<string, string> RejectedTokens() => new()
    {
        { "wrong audience", TestEntra.Token(Ids.AcmeDirectory, Oid, aud: "api://some-other-api") },
        { "missing scope", TestEntra.Token(Ids.AcmeDirectory, Oid, scp: "User.Read") },
        { "no scp (app-only)", TestEntra.Token(Ids.AcmeDirectory, Oid, scp: null, roles: ["PlatformAdmin"]) },
        { "idtyp=app", TestEntra.Token(Ids.AcmeDirectory, Oid, idtyp: "app") },
        { "expired", TestEntra.Token(Ids.AcmeDirectory, Oid, expires: DateTime.UtcNow.AddMinutes(-10)) },
        { "issuer names another directory", TestEntra.Token(Ids.AcmeDirectory, Oid, issuer: $"https://login.microsoftonline.com/{Ids.GuestDirectory}/v2.0") },
        { "issuer not Entra", TestEntra.Token(Ids.AcmeDirectory, Oid, issuer: "https://evil.example/" + Ids.AcmeDirectory) },
        { "not signed by Entra", new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = $"https://login.microsoftonline.com/{Ids.AcmeDirectory}/v2.0",
                Audience = Ids.ApiClientId,
                Expires = DateTime.UtcNow.AddMinutes(10),
                Claims = new Dictionary<string, object> { ["tid"] = Ids.AcmeDirectory, ["oid"] = Oid, ["scp"] = "Cho.Token" },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)), SecurityAlgorithms.RsaSha256),
            }) },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Unacceptable_entra_tokens_are_401_invalid_token(string why, string token)
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");

        var response = await As(token).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, why);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_token");
        Directory.AuthorizationHeaders.Should().BeEmpty("nothing is looked up for a refused token");
    }

    [Fact]
    public async Task Missing_token_is_401_invalid_token_on_both_endpoints()
    {
        var client = _factory.CreateClient();
        var exchange = await client.ExchangeAsync("acme");
        exchange.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await exchange.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_token");

        (await client.GetAsync("/v1/token/tenants")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task V1_entra_token_with_app_id_uri_audience_is_accepted()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "ClaimsExaminer");
        var v1 = TestEntra.Token(Ids.AcmeDirectory, Oid, aud: Ids.ApiAppIdUri, issuer: $"https://sts.windows.net/{Ids.AcmeDirectory}/");

        (await As(v1).ExchangeAsync("acme")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        (await _factory.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Membership by oid + tid ─────────────────────────────────────────

    [Fact]
    public async Task Oid_match_with_a_different_recorded_directory_is_refused()
    {
        // Same oid value, but the record belongs to the Acme directory and the
        // token comes from another one.
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "pat@acme.example");

        var response = await As(token).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("no_access");
    }

    [Fact]
    public async Task Linked_guest_from_another_directory_is_admitted_by_oid_and_tid()
    {
        var user = Directory.AddUser("acme", "consultant@guest.example", Oid, Ids.GuestDirectory, "Active", "ComplianceViewer");
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "consultant@guest.example");

        var response = await As(token).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("user").GetProperty("id").GetString().Should().Be(user.Id);
    }

    [Fact]
    public async Task Legacy_oid_only_link_is_honoured_from_the_tenants_own_directory_and_records_tid()
    {
        var user = Directory.AddUser("acme", "pat@acme.example", Oid, null, "Active", "ClaimsExaminer");

        var response = await As(AcmeUserToken()).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        user.AzureAdTenantId.Should().Be(Ids.AcmeDirectory);
        Directory.Links.Should().ContainSingle(l => l.UserId == user.Id && l.Tid == Ids.AcmeDirectory);
    }

    [Fact]
    public async Task Legacy_oid_only_link_is_not_honoured_from_another_directory()
    {
        // e.g. written by the portal's old email backfill for a guest.
        Directory.AddUser("acme", "pat@acme.example", Oid, null, "Active", "TenantAdmin");
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "pat@acme.example");

        (await As(token).ExchangeAsync("acme")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_records_linked_to_one_identity_in_a_tenant_are_refused_even_for_platform_admin()
    {
        Directory.AddUser("beta", "a@cho.example", Oid, Ids.PlatformDirectory, "Active", "ClaimsExaminer");
        Directory.AddUser("beta", "b@cho.example", Oid, Ids.PlatformDirectory, "Active", "TenantAdmin");

        (await As(PlatformToken()).ExchangeAsync("beta")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── First-login email linking ───────────────────────────────────────

    [Fact]
    public async Task Unlinked_user_is_linked_by_email_from_the_tenants_own_directory()
    {
        var user = Directory.AddUser("acme", "Pat@Acme.Example", null, null, "Active", "ClaimsExaminer");

        var response = await As(AcmeUserToken(username: "pat@acme.example")).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("user").GetProperty("id").GetString().Should().Be(user.Id);
        Directory.Links.Should().ContainSingle().Which.Should().Be(("acme", user.Id, Oid, Ids.AcmeDirectory));
    }

    [Fact]
    public async Task Email_match_on_a_user_already_linked_to_another_oid_is_refused()
    {
        Directory.AddUser("acme", "pat@acme.example", "bbbbbbbb-0000-0000-0000-000000000002", Ids.AcmeDirectory, "Active", "TenantAdmin");

        var response = await As(AcmeUserToken()).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task Guest_from_another_directory_is_not_linked_by_email()
    {
        Directory.AddUser("acme", "pat@acme.example", null, null, "Active", "TenantAdmin");
        // A directory the attacker controls can issue tokens for any address.
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "pat@acme.example");

        var response = await As(token).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("no_access");
        Directory.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_linking_needs_a_registered_directory_on_the_tenant()
    {
        Directory.AddTenant("noaad", null);
        Directory.AddUser("noaad", "pat@acme.example", null, null, "Active", "TenantAdmin");

        (await As(AcmeUserToken()).ExchangeAsync("noaad")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.Links.Should().BeEmpty();
    }

    // ── Status ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Locked")]
    public async Task Inactive_user_is_403(string status)
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, status, "TenantAdmin");

        var response = await As(AcmeUserToken()).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("no_access");
    }

    [Fact]
    public async Task Inactive_tenant_is_403_even_for_a_linked_active_user()
    {
        Directory.AddUser("dormant", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");

        (await As(AcmeUserToken()).ExchangeAsync("dormant")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unknown_tenant_is_403()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");

        (await As(AcmeUserToken()).ExchangeAsync("nope")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Member_of_one_tenant_cannot_enter_another()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");

        (await As(AcmeUserToken()).ExchangeAsync("beta")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Roles ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Active_user_with_no_roles_gets_an_empty_token()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active");

        var response = await As(AcmeUserToken()).ExchangeAsync("acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("roles").GetArrayLength().Should().Be(0);
        body.GetProperty("permissions").GetArrayLength().Should().Be(0);
        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Claims.Where(c => c.Type is "roles" or "permissions").Should().BeEmpty();
    }

    [Fact]
    public async Task PlatformAdmin_role_assigned_inside_a_tenant_is_not_honoured()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "PlatformAdmin", "ClaimsExaminer");

        var body = await (await As(AcmeUserToken()).ExchangeAsync("acme")).JsonAsync();

        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("ClaimsExaminer");
        body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().NotContain(p => p!.StartsWith("platform:"));
    }

    // ── Platform administration ─────────────────────────────────────────

    private static string PlatformToken(string tid = Ids.PlatformDirectory, string[]? roles = null)
        => TestEntra.Token(tid, Oid, username: "ops@cho.example", roles: roles ?? ["PlatformAdmin"], name: "Ops Person");

    [Fact]
    public async Task Platform_admin_may_enter_any_active_tenant_with_a_platform_subject()
    {
        var response = await As(PlatformToken()).ExchangeAsync("beta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("PlatformAdmin");
        body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain("platform:admin");
        body.GetProperty("user").GetProperty("id").GetString().Should().Be($"platform:{Ids.PlatformDirectory}:{Oid}");
        new JsonWebToken(body.GetProperty("access_token").GetString()).Subject
            .Should().Be($"platform:{Ids.PlatformDirectory}:{Oid}");
    }

    [Fact]
    public async Task Platform_admin_with_a_tenant_user_record_uses_its_id_and_roles_plus_platform_admin()
    {
        var user = Directory.AddUser("beta", "ops@cho.example", Oid, Ids.PlatformDirectory, "Active", "ClaimsSupervisor");

        var body = await (await As(PlatformToken()).ExchangeAsync("beta")).JsonAsync();

        body.GetProperty("user").GetProperty("id").GetString().Should().Be(user.Id);
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().BeEquivalentTo("ClaimsSupervisor", "PlatformAdmin");
    }

    [Fact]
    public async Task Platform_admin_cannot_enter_an_inactive_tenant()
    {
        (await As(PlatformToken()).ExchangeAsync("dormant")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlatformAdmin_app_role_from_another_directory_is_refused()
    {
        // A customer directory can assign this app's roles to its own users.
        var response = await As(PlatformToken(tid: Ids.AcmeDirectory)).ExchangeAsync("beta");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_directory_without_the_app_role_is_refused()
    {
        var response = await As(PlatformToken(roles: ["SomethingElse"])).ExchangeAsync("beta");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_admin_lists_all_active_tenants()
    {
        var list = await (await As(PlatformToken()).GetAsync("/v1/token/tenants")).JsonAsync();

        list.EnumerateArray().Select(t => t.GetProperty("tenantId").GetString())
            .Should().BeEquivalentTo("acme", "beta", "cho");
    }

    // ── Home tenant resolution ──────────────────────────────────────────

    [Fact]
    public async Task Without_a_tenant_the_home_tenant_is_used()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "ClaimsExaminer");
        Directory.AddUser("beta", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "Finance");

        var response = await _factory.ClientWith(AcmeUserToken()).PostAsync("/v1/token/exchange", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("tenant_id").GetString().Should().Be("acme");
    }

    [Fact]
    public async Task Without_a_tenant_a_single_membership_is_used()
    {
        Directory.AddUser("beta", "consultant@guest.example", Oid, Ids.GuestDirectory, "Active", "Finance");
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "consultant@guest.example");

        var response = await As(token).ExchangeAsync(null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("tenant_id").GetString().Should().Be("beta");
    }

    [Fact]
    public async Task Ambiguous_home_tenant_is_409_with_the_choices()
    {
        Directory.AddUser("acme", "consultant@guest.example", Oid, Ids.GuestDirectory, "Active", "Finance");
        Directory.AddUser("beta", "consultant@guest.example", Oid, Ids.GuestDirectory, "Active", "Finance");
        var token = TestEntra.Token(Ids.GuestDirectory, Oid, username: "consultant@guest.example");

        var response = await As(token).ExchangeAsync(null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.JsonAsync();
        body.GetProperty("error").GetString().Should().Be("tenant_required");
        body.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("tenantId").GetString())
            .Should().BeEquivalentTo("acme", "beta");
        body.GetProperty("tenants")[0].GetProperty("azureTenantId").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task Tenants_lists_memberships_and_email_linkable_home_tenants_only()
    {
        Directory.AddUser("acme", "pat@acme.example", null, null, "Active", "ClaimsExaminer"); // linkable at home
        Directory.AddUser("beta", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "Finance"); // linked guest
        Directory.AddUser("cho", "pat@acme.example", null, null, "Active", "Finance"); // not linkable: other directory

        var response = await As(AcmeUserToken()).GetAsync("/v1/token/tenants");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.JsonAsync();
        list.EnumerateArray().Select(t => t.GetProperty("tenantId").GetString()).Should().BeEquivalentTo("acme", "beta");
        Directory.Links.Should().BeEmpty("listing never links");
    }

    // ── Fail closed ─────────────────────────────────────────────────────

    [Fact]
    public async Task Tenant_service_down_is_503_unavailable()
    {
        Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "TenantAdmin");
        Directory.Down = true;

        var exchange = await As(AcmeUserToken()).ExchangeAsync("acme");
        exchange.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await exchange.JsonAsync()).GetProperty("error").GetString().Should().Be("unavailable");

        var tenants = await As(AcmeUserToken()).GetAsync("/v1/token/tenants");
        tenants.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Platform_admin_gets_no_token_when_tenant_service_is_down()
    {
        Directory.Down = true;

        (await As(PlatformToken()).ExchangeAsync("beta")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── The issued token works against a CHO service ───────────────────

    [Fact]
    public async Task Issued_token_validates_under_shared_ChoAuth_with_subject_tenant_and_permissions()
    {
        var user = Directory.AddUser("acme", "pat@acme.example", Oid, Ids.AcmeDirectory, "Active", "ClaimsExaminer");
        var body = await (await As(AcmeUserToken()).ExchangeAsync("acme")).JsonAsync();
        var choToken = body.GetProperty("access_token").GetString()!;

        // Services trust the issuer by public key, as published at jwks.json.
        var jwks = await (await _factory.CreateClient().GetAsync("/.well-known/jwks.json")).JsonAsync();
        var jwk = new JsonWebKey(jwks.GetProperty("keys")[0].GetRawText());
        jwk.D.Should().BeNull("only public material is published");
        jwk.KeyId.Should().Be(new JsonWebToken(choToken).Kid);
        var publicPem = PemKeys.PublicKeyPem(jwk);

        await using var service = await ChoServiceAsync(publicPem);
        var client = service.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", choToken);

        var whoami = await (await client.GetAsync("/whoami")).JsonAsync();
        whoami.GetProperty("sub").GetString().Should().Be(user.Id);
        whoami.GetProperty("tenant").GetString().Should().Be("acme");
        whoami.GetProperty("claimsRead").GetBoolean().Should().BeTrue();
        whoami.GetProperty("membersWrite").GetBoolean().Should().BeFalse();
        whoami.GetProperty("platformAdmin").GetBoolean().Should().BeFalse();
        whoami.GetProperty("isService").GetBoolean().Should().BeFalse();

        // The Entra token itself is not a CHO token.
        var raw = service.GetTestClient();
        raw.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AcmeUserToken());
        (await raw.GetAsync("/whoami")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A minimal CHO backend service using the shared authentication, in Production mode.</summary>
    private static async Task<WebApplication> ChoServiceAsync(string publicKeyPem)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        // Only this service's settings (not the token-service appsettings.json in the test output).
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChoAuth:Audience"] = "cho-api",
            ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service",
            ["ChoAuth:Issuers:0:PublicKeyPem"] = publicKeyPem,
        });
        builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseChoAuthentication();
        app.MapGet("/whoami", (ICurrentActor actor) => Results.Json(new
        {
            sub = actor.UserId,
            tenant = actor.TenantId,
            claimsRead = actor.HasPermission("claims:read"),
            membersWrite = actor.HasPermission("members:write"),
            platformAdmin = actor.HasPermission("platform:admin"),
            isService = actor.IsService,
        }))
        // A diagnostic endpoint for any authenticated caller, declared as such:
        // an unannotated minimal API in a service with no default permissions
        // is denied (the shared fallback policy).
        .RequireAuthorization();
        await app.StartAsync();
        return app;
    }
}
