using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// smart-auth-service tokens carry a tenant (and, for members, a patient)
/// taken from server-side bindings only. Each test works in its own fresh
/// tenant so tests cannot see each other's bindings.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartTenancyTests
{
    private const string MemberScope = "openid launch/patient patient/*.read";
    private const string ValidNpi = "1234567893";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartTenancyTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    // ── Members ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MemberToken_CarriesTheMappedTenantAndPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "mbr-1001", username: "alice-" + tenant);

        var token = SmartAuthDriver.Read(await _smart.MemberAccessTokenAsync(browser, app));

        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "patient").Should().Be("mbr-1001");
        SmartAuthDriver.Claim(token, "sub").Should().Be("alice-" + tenant);
        SmartAuthDriver.Claim(token, "fhirUser").Should().Be("Patient/mbr-1001");
        token.Claims.Should().NotContain(c => c.Type.StartsWith("cho_"),
            "the binding bookkeeping claims stay in the code and refresh token");
    }

    [Fact]
    public async Task UserWithNoMapping_GetsNoToken()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await _smart.SignInAsync("nobody-" + tenant);

        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, MemberScope);

        outcome.Code.Should().BeNull();
        outcome.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task UserWithNoMapping_GetsNoToken_EvenAskingOnlyForOpenId()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "patient/*.read");
        var browser = await _smart.SignInAsync("nobody2-" + tenant);

        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, "openid");

        outcome.Code.Should().BeNull();
        outcome.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task HeadersAndRequestParameters_CannotChooseTenantOrPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "mbr-2002");

        void Spoof(HttpRequestMessage r)
        {
            r.Headers.Add("X-Tenant-ID", "victim-tenant");
            r.Headers.Add("X-Dev-Tenant-ID", "victim-tenant");
        }

        var spoofed = new Dictionary<string, string>
        {
            ["tenant_id"] = "victim-tenant",
            ["tenant"] = "victim-tenant",
            ["patient"] = "victim-patient",
        };

        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, MemberScope, extra: spoofed, tamper: Spoof);
        outcome.Error.Should().BeNull();
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome, extra: spoofed, tamper: Spoof);
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "patient").Should().Be("mbr-2002");
    }

    [Fact]
    public async Task LaunchRequest_CannotSetAMembersPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (patientApp, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var (ehrApp, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "launch", "launch/patient", "user/*.read", "patient/*.read");
        var member = await _smart.LinkedMemberAsync(tenant, "mbr-3003");

        // A launch someone in the tenant registered for ANOTHER patient.
        var launch = await RegisterLaunchAsync(tenant, ehrApp, "someone-else");

        var viaPatientApp = await SmartAuthDriver.AuthorizeAsync(member, patientApp, MemberScope, launch: launch);
        viaPatientApp.Code.Should().BeNull("a member's patient comes from their binding, never a launch");
        viaPatientApp.Error.Should().Be("access_denied");

        var launch2 = await RegisterLaunchAsync(tenant, ehrApp, "someone-else");
        var viaEhrApp = await SmartAuthDriver.AuthorizeAsync(member, ehrApp, "openid launch patient/*.read", launch: launch2);
        viaEhrApp.Code.Should().BeNull("a member is not a provider user");
        viaEhrApp.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task Refresh_AfterTheMemberLinkIsRevoked_GetsNoToken()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "offline_access", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "mbr-4004");

        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, MemberScope + " offline_access");
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var refresh = body.GetProperty("refresh_token").GetString()!;

        // Refresh works while the link stands, and keeps tenant and patient.
        var (okStatus, okBody) = await _smart.RefreshAsync(app, refresh);
        okStatus.Should().Be(HttpStatusCode.OK, okBody.ToString());
        var refreshed = SmartAuthDriver.Read(okBody.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(refreshed, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(refreshed, "patient").Should().Be("mbr-4004");

        var admin = _smart.Admin(tenant, ChoRolePermissions.EnrollmentSpecialist);
        var links = await admin.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-links");
        var linkId = links.EnumerateArray().Single().GetProperty("id").GetString();
        (await admin.DeleteAsync($"/api/admin/smart/member-links/{linkId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (deniedStatus, deniedBody) = await _smart.RefreshAsync(app, okBody.GetProperty("refresh_token").GetString()!);
        deniedStatus.Should().Be(HttpStatusCode.BadRequest);
        deniedBody.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task EnrolmentCode_IsSingleUse_AndAnIdentityBindsToOneMemberOnly()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var code = await _smart.IssueMemberCodeAsync(tenant, "mbr-5005");
        var first = await _smart.SignInAsync("first-" + tenant);
        (await SmartAuthDriver.RedeemAsync(first, code)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await _smart.SignInAsync("second-" + tenant);
        (await SmartAuthDriver.RedeemAsync(second, code)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var another = await _smart.IssueMemberCodeAsync(SmartAuthDriver.NewTenant(), "mbr-other");
        (await SmartAuthDriver.RedeemAsync(first, another)).StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a member identity is bound to exactly one member id and tenant");
    }

    [Fact]
    public async Task MemberApp_FromAnotherTenant_GetsNoToken()
    {
        var tenantA = SmartAuthDriver.NewTenant();
        var tenantB = SmartAuthDriver.NewTenant();
        var (appOfB, _) = await _smart.RegisterClientAsync(tenantB, "patient-app", "openid", "launch/patient", "patient/*.read");
        var memberOfA = await _smart.LinkedMemberAsync(tenantA, "mbr-6006");

        var outcome = await SmartAuthDriver.AuthorizeAsync(memberOfA, appOfB, MemberScope);

        outcome.Code.Should().BeNull("the app is registered to another tenant");
        outcome.Error.Should().Be("access_denied");
    }

    // ── Providers and EHR launch ──────────────────────────────────────────────

    [Fact]
    public async Task ProviderEhrLaunch_TokenCarriesProvidersTenantAndTheLaunchPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (ehrApp, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "fhirUser", "launch", "launch/patient", "user/*.read");
        var provider = await _smart.LinkedProviderAsync(tenant, "prov-77", ValidNpi);
        var launch = await RegisterLaunchAsync(tenant, ehrApp, "mbr-7007");

        var outcome = await SmartAuthDriver.AuthorizeAsync(provider, ehrApp, "openid fhirUser launch user/*.read", launch: launch);
        outcome.Error.Should().BeNull();
        var (status, body) = await _smart.ExchangeCodeAsync(ehrApp, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "sub").Should().Be("prov-77");
        SmartAuthDriver.Claim(token, "fhirUser").Should().Be("Practitioner/prov-77");
        SmartAuthDriver.Claim(token, "npi").Should().Be(ValidNpi);
        SmartAuthDriver.Claim(token, "patient").Should().Be("mbr-7007");
    }

    [Fact]
    public async Task ProviderToken_WithoutLaunch_HasNoPatient_AndCannotTakePatientScopes()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (ehrApp, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "launch/patient", "user/*.read", "patient/*.read");
        var provider = await _smart.LinkedProviderAsync(tenant, "prov-88", ValidNpi);

        var userOnly = await SmartAuthDriver.AuthorizeAsync(provider, ehrApp, "openid user/*.read");
        var (status, body) = await _smart.ExchangeCodeAsync(ehrApp, userOnly);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "patient").Should().BeNull();

        var patientScoped = await SmartAuthDriver.AuthorizeAsync(provider, ehrApp, "openid user/*.read patient/*.read");
        patientScoped.Error.Should().Be("access_denied", "patient context for a provider comes only from an EHR launch");
    }

    [Fact]
    public async Task LaunchNamingAPractitioner_IsHonouredOnlyForThatProvider()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (ehrApp, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "launch", "user/*.read");
        var intended = await _smart.LinkedProviderAsync(tenant, "prov-intended", ValidNpi);
        var other = await _smart.LinkedProviderAsync(tenant, "prov-other", ValidNpi);
        var launch = await RegisterLaunchAsync(tenant, ehrApp, "mbr-4242", practitionerId: "prov-intended");

        // Another provider of the same tenant, same app, holding the token: refused.
        var stolen = await SmartAuthDriver.AuthorizeAsync(other, ehrApp, "openid launch user/*.read", launch: launch);
        stolen.Code.Should().BeNull();
        stolen.Error.Should().Be("access_denied");

        // ...and the launch is still there for the provider it was made for.
        var outcome = await SmartAuthDriver.AuthorizeAsync(intended, ehrApp, "openid launch user/*.read", launch: launch);
        outcome.Error.Should().BeNull();
        var (status, body) = await _smart.ExchangeCodeAsync(ehrApp, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "patient").Should().Be("mbr-4242");
        SmartAuthDriver.Claim(token, "sub").Should().Be("prov-intended");
    }

    [Fact]
    public async Task LaunchRegistration_RefusesAMalformedPractitionerId()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (ehrApp, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "launch", "user/*.read");
        (await _smart.Admin(tenant, ChoRolePermissions.MemberServices)
                .PostAsJsonAsync("/launch", new { patientId = "p1", clientId = ehrApp, practitionerId = "bad id/../x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LaunchFromAnotherTenant_IsNotHonoured()
    {
        var tenantA = SmartAuthDriver.NewTenant();
        var tenantB = SmartAuthDriver.NewTenant();
        var (appA, _) = await _smart.RegisterClientAsync(tenantA, "provider-app", "openid", "launch", "user/*.read");
        var (appB, _) = await _smart.RegisterClientAsync(tenantB, "provider-app", "openid", "launch", "user/*.read");
        var providerB = await _smart.LinkedProviderAsync(tenantB, "prov-b", ValidNpi);

        var launchOfA = await RegisterLaunchAsync(tenantA, appA, "mbr-of-a");
        var outcome = await SmartAuthDriver.AuthorizeAsync(providerB, appB, "openid launch user/*.read", launch: launchOfA);

        outcome.Code.Should().BeNull();
        outcome.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task LaunchRegistration_TakesItsTenantFromTheChoToken_NotAHeader()
    {
        var tenantA = SmartAuthDriver.NewTenant();
        var tenantB = SmartAuthDriver.NewTenant();
        var (appA, _) = await _smart.RegisterClientAsync(tenantA, "provider-app", "openid", "launch", "user/*.read");
        var body = new { patientId = "mbr-1", clientId = appA };

        // No token, header only: refused.
        var anonymous = _smart.Anonymous();
        anonymous.DefaultRequestHeaders.Add("X-Tenant-ID", tenantA);
        (await anonymous.PostAsJsonAsync("/launch", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Tenant B's token naming tenant A in the header: conflict.
        var b = _smart.Admin(tenantB, ChoRolePermissions.MemberServices);
        b.DefaultRequestHeaders.Add("X-Tenant-ID", tenantA);
        (await b.PostAsJsonAsync("/launch", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Tenant B's token for tenant A's client: the client is not B's.
        (await _smart.Admin(tenantB, ChoRolePermissions.MemberServices).PostAsJsonAsync("/launch", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Tenant A's token: accepted.
        (await _smart.Admin(tenantA, ChoRolePermissions.MemberServices).PostAsJsonAsync("/launch", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Clients (client_credentials) ──────────────────────────────────────────

    [Fact]
    public async Task ClientCredentialsToken_CarriesTheRegisteredTenant()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await _smart.RegisterClientAsync(tenant, "backend", "system/*.read");

        var (status, body) = await _smart.ClientCredentialsAsync(clientId, secret!, "system/*.read",
            r => r.Headers.Add("X-Tenant-ID", "victim-tenant"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "sub").Should().Be(clientId);
        SmartAuthDriver.Claim(token, "patient").Should().BeNull();
    }

    [Fact]
    public async Task ClientCredentials_ForAClientWithNoTenantRegistration_GetsNoToken()
    {
        // An OpenIddict application created behind the admin API's back has
        // no tenant binding, so it can authenticate but gets no token.
        using var scope = _fixture.Factory.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var clientId = "unbound-" + Guid.NewGuid().ToString("N")[..8];
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = "unbound-secret",
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "system/*.read",
            },
        });

        var (status, body) = await _smart.ClientCredentialsAsync(clientId, "unbound-secret", "system/*.read");

        status.Should().NotBe(HttpStatusCode.OK);
        body.TryGetProperty("access_token", out _).Should().BeFalse();
        body.GetProperty("error").GetString().Should().Be("unauthorized_client");
    }

    [Fact]
    public async Task DeletingAClientRegistration_StopsItsTokens()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await _smart.RegisterClientAsync(tenant, "backend", "system/*.read");
        (await _smart.Admin(tenant).DeleteAsync($"/api/admin/smart/clients/{clientId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (status, _) = await _smart.ClientCredentialsAsync(clientId, secret!, "system/*.read");
        status.Should().NotBe(HttpStatusCode.OK);
    }

    // ── Admin API: CHO permissions, tenant scoping, actor ─────────────────────

    public static TheoryData<string, string, object, HttpStatusCode> PermissionMatrix => new()
    {
        // Members: members:write
        { "/api/admin/smart/member-enrolments", ChoRolePermissions.EnrollmentSpecialist, new { memberId = "m-1" }, HttpStatusCode.Created },
        { "/api/admin/smart/member-enrolments", ChoRolePermissions.MemberServices,       new { memberId = "m-1" }, HttpStatusCode.Forbidden },
        { "/api/admin/smart/member-enrolments", ChoRolePermissions.ProviderRelations,    new { memberId = "m-1" }, HttpStatusCode.Forbidden },
        // Providers: providers:write
        { "/api/admin/smart/provider-enrolments", ChoRolePermissions.ProviderRelations,    new { providerId = "p-1", npi = ValidNpi }, HttpStatusCode.Created },
        { "/api/admin/smart/provider-enrolments", ChoRolePermissions.EnrollmentSpecialist, new { providerId = "p-1", npi = ValidNpi }, HttpStatusCode.Forbidden },
        // Clients: settings:manage
        { "/api/admin/smart/clients", ChoRolePermissions.TenantAdmin,       new { kind = "backend", scopes = new[] { "system/*.read" } }, HttpStatusCode.Created },
        { "/api/admin/smart/clients", ChoRolePermissions.ProviderRelations, new { kind = "backend", scopes = new[] { "system/*.read" } }, HttpStatusCode.Forbidden },
        { "/api/admin/smart/clients", ChoRolePermissions.ClaimsSupervisor,  new { kind = "backend", scopes = new[] { "system/*.read" } }, HttpStatusCode.Forbidden },
    };

    [Theory]
    [MemberData(nameof(PermissionMatrix))]
    public async Task AdminEndpoints_EnforceChoPermissions(string path, string role, object body, HttpStatusCode expected)
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant(), role).PostAsJsonAsync(path, body);
        resp.StatusCode.Should().Be(expected, await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/admin/smart/member-links")]
    [InlineData("/api/admin/smart/provider-users")]
    [InlineData("/api/admin/smart/clients")]
    public async Task AdminEndpoints_RequireAChoToken(string path)
    {
        var anonymous = _smart.Anonymous();
        anonymous.DefaultRequestHeaders.Add("X-Tenant-ID", "demo-tenant");
        (await anonymous.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoints_RefuseServiceTokens()
    {
        var service = _smart.Anonymous();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("some-service", SmartAuthDriver.NewTenant()));

        (await service.PostAsJsonAsync("/api/admin/smart/member-enrolments", new { memberId = "m-1" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a person, not a service, binds identities");
    }

    [Fact]
    public async Task AdminEndpoints_AreTenantScoped()
    {
        var tenantA = SmartAuthDriver.NewTenant();
        var tenantB = SmartAuthDriver.NewTenant();
        await _smart.LinkedMemberAsync(tenantA, "mbr-a");
        await _smart.LinkedProviderAsync(tenantA, "prov-a", ValidNpi);
        var (clientA, _) = await _smart.RegisterClientAsync(tenantA, "backend", "system/*.read");

        var adminA = _smart.Admin(tenantA);
        var adminB = _smart.Admin(tenantB);

        var memberLinkA = (await adminA.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-links"))
            .EnumerateArray().Single().GetProperty("id").GetString();
        var providerLinkA = (await adminA.GetFromJsonAsync<JsonElement>("/api/admin/smart/provider-users"))
            .EnumerateArray().Single().GetProperty("id").GetString();

        (await adminB.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-links")).GetArrayLength().Should().Be(0);
        (await adminB.GetFromJsonAsync<JsonElement>("/api/admin/smart/provider-users")).GetArrayLength().Should().Be(0);
        (await adminB.GetFromJsonAsync<JsonElement>("/api/admin/smart/clients")).GetArrayLength().Should().Be(0);
        (await adminB.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-enrolments")).GetArrayLength().Should().Be(0);

        (await adminB.DeleteAsync($"/api/admin/smart/member-links/{memberLinkA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.DeleteAsync($"/api/admin/smart/provider-users/{providerLinkA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.GetAsync($"/api/admin/smart/clients/{clientA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.DeleteAsync($"/api/admin/smart/clients/{clientA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // B naming A in the header is a conflict, not a switch.
        var spoof = _smart.Admin(tenantB);
        spoof.DefaultRequestHeaders.Add("X-Tenant-ID", tenantA);
        (await spoof.GetAsync("/api/admin/smart/member-links")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A's bindings are untouched.
        (await adminA.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-links")).EnumerateArray().Single()
            .GetProperty("status").GetString().Should().Be("Active");
        (await adminA.GetAsync($"/api/admin/smart/clients/{clientA}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminWrites_RecordTheActorAndTenantFromTheToken_NotTheBody()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var admin = _smart.AdminAs(tenant, "enroller-7", ChoRolePermissions.EnrollmentSpecialist);

        var resp = await admin.PostAsJsonAsync("/api/admin/smart/member-enrolments", new
        {
            memberId = "mbr-8",
            tenantId = "victim-tenant",
            createdBy = "someone-else",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        created.GetProperty("tenantId").GetString().Should().Be(tenant);
        created.GetProperty("createdBy").GetString().Should().Be("enroller-7");

        var browser = await _smart.SignInAsync("eight-" + tenant);
        (await SmartAuthDriver.RedeemAsync(browser, created.GetProperty("code").GetString()!)).StatusCode.Should().Be(HttpStatusCode.OK);
        var link = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/smart/member-links")).EnumerateArray().Single();
        link.GetProperty("tenantId").GetString().Should().Be(tenant);
        link.GetProperty("memberId").GetString().Should().Be("mbr-8");
        link.GetProperty("createdBy").GetString().Should().Be("enroller-7");
    }

    [Fact]
    public async Task BindingChangesAndTokenRefusals_AreAudited_WithTheActorFromTheToken()
    {
        var capture = new CapturingLoggerProvider();
        await using var host = _fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(logging => logging.AddProvider(capture)));
        var smart = new SmartAuthDriver(host);
        var tenant = SmartAuthDriver.NewTenant();

        (await smart.AdminAs(tenant, "auditor-1", ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync("/api/admin/smart/member-enrolments", new { memberId = "m-9", createdBy = "forged" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var (app, _) = await smart.RegisterClientAsync(tenant, "patient-app", "openid", "patient/*.read");
        await SmartAuthDriver.AuthorizeAsync(await smart.SignInAsync("unbound-" + tenant), app, "openid patient/*.read");

        var audit = capture.Lines("CloudHealthOffice.SmartAuth.Audit");
        audit.Should().Contain(l => l.Contains("member-enrolment-issued") && l.Contains($"tenant={tenant}") && l.Contains("actor=auditor-1"));
        audit.Should().Contain(l => l.Contains("client-registered") && l.Contains($"tenant={tenant}") && l.Contains($"actor=admin-{tenant}"));
        audit.Should().Contain(l => l.Contains("refused") && l.Contains("no_member_mapping") && l.Contains($"client={app}"));
        audit.Should().NotContain(l => l.Contains("forged"));
    }

    // ── Development seed (demo-tenant) ────────────────────────────────────────
    // These use only the seeded demo clients and identities, so they also run
    // unchanged against the previous smart-auth-service — where they fail.

    private const string DemoRedirect = "http://localhost:4200/callback";

    [Fact]
    public async Task SeededDemoMember_TokenCarriesDemoTenantAndTheirPatient()
    {
        var browser = await _smart.SignInAsync("demo-member");
        var token = SmartAuthDriver.Read(
            await _smart.MemberAccessTokenAsync(browser, "smart-patient-app", redirectUri: DemoRedirect));

        SmartAuthDriver.Claim(token, "tenant_id").Should().Be("demo-tenant");
        SmartAuthDriver.Claim(token, "patient").Should().Be("pat-001",
            "the patient is the member id the identity is bound to, not the username");
    }

    [Fact]
    public async Task UnboundUsername_OnTheSeededApp_GetsNoToken()
    {
        var browser = await _smart.SignInAsync("pat-002");

        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, "smart-patient-app", MemberScope, redirectUri: DemoRedirect);

        outcome.Code.Should().BeNull("a username is not a member binding");
        outcome.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task SeededBackendClient_ClientCredentialsToken_CarriesDemoTenant()
    {
        var (status, body) = await _smart.ClientCredentialsAsync(
            "cho-payer-system", "system-secret-change-in-prod", "system/*.read",
            r => r.Headers.Add("X-Tenant-ID", "victim-tenant"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "tenant_id")
            .Should().Be("demo-tenant");
    }

    [Fact]
    public async Task LaunchRegistration_WithOnlyATenantHeader_IsRefused()
    {
        var anonymous = _smart.Anonymous();
        anonymous.DefaultRequestHeaders.Add("X-Tenant-ID", "demo-tenant");

        var resp = await anonymous.PostAsJsonAsync("/launch", new { patientId = "pat-002", clientId = "cho-ehr-app" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SeededDemoMember_LaunchNamingAnotherPatient_IsRefused()
    {
        var launch = await RegisterLaunchAsync("demo-tenant", "cho-ehr-app", "pat-002");
        var browser = await _smart.SignInAsync("demo-member");

        var outcome = await SmartAuthDriver.AuthorizeAsync(
            browser, "smart-patient-app", MemberScope, launch: launch, redirectUri: DemoRedirect);

        outcome.Code.Should().BeNull("a launch cannot put another patient into a member's token");
        outcome.Error.Should().Be("access_denied");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> RegisterLaunchAsync(string tenant, string clientId, string patientId, string? practitionerId = null)
    {
        var resp = await _smart.Admin(tenant, ChoRolePermissions.MemberServices)
            .PostAsJsonAsync("/launch", new { patientId, clientId, practitionerId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("launch").GetString()!;
    }
}
