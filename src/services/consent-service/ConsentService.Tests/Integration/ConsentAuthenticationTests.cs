using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using ConsentService.Controllers;
using ConsentService.Models;

namespace ConsentService.Tests.Integration;

/// <summary>
/// Pins the shared CHO authentication contract for consent-service over the
/// real Program pipeline: the tenant and the acting user come from the
/// validated token only. A body naming a grantor, status or timestamps and a
/// header naming another tenant are ignored or rejected.
/// </summary>
public class ConsentAuthenticationTests : IClassFixture<ConsentLifecycleSmokeTests.Factory>
{
    private const string TokenUser = "token-user-42";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConsentLifecycleSmokeTests.Factory _factory;

    public ConsentAuthenticationTests(ConsentLifecycleSmokeTests.Factory factory)
    {
        _factory = factory;
    }

    // Always pass a subject: a lone string binds to the (subject, params roles)
    // overload and would silently mint a TenantAdmin token.
    private HttpClient NewClient(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(TokenUser, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static string NewTenant() => "tenant-auth-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<Consent> ReadConsentAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"request should succeed, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Consent>(body, Json)!;
    }

    private IEnumerable<ConsentEvent> EventsFor(string tenant)
        => _factory.Repo.SnapshotEvents().Where(e => e.TenantId == tenant);

    // ── Actor from token ────────────────────────────────────────────────

    [Fact]
    public async Task Create_BodyGrantorStatusAndTimestamps_AreIgnored()
    {
        var tenant = NewTenant();
        var client = NewClient(tenant);
        var forged = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var created = await ReadConsentAsync(await client.PostAsJsonAsync("/api/v1/members/M1/consents", new JsonObject
        {
            ["consentType"] = "GeneralAuthorization",
            ["grantedBy"] = "attacker",
            ["status"] = "Active",
            ["createdAt"] = forged.ToString("o"),
            ["activatedBy"] = "attacker",
            ["activatedAt"] = forged.ToString("o"),
            ["revokedBy"] = "attacker",
            ["tenantId"] = "tenant-victim",
        }));

        var stored = (await _factory.Repo.GetByIdAsync(tenant, "M1", created.Id))!;
        stored.GrantedBy.Should().Be(TokenUser);
        stored.Status.Should().Be(ConsentStatus.Draft);
        stored.TenantId.Should().Be(tenant);
        stored.CreatedAt.Should().BeAfter(forged.AddYears(1));
        stored.ActivatedBy.Should().BeNull();
        stored.ActivatedAt.Should().BeNull();
        stored.RevokedBy.Should().BeNull();
        EventsFor(tenant).Should().ContainSingle().Which.ActorId.Should().Be(TokenUser);
        _factory.Publisher.Calls.Should().Contain(c => c.ConsentId == created.Id && c.Actor == TokenUser);
    }

    [Fact]
    public async Task ActivateAndRevoke_RecordTokenActor_IgnoringBodyActor()
    {
        var tenant = NewTenant();
        var client = NewClient(tenant);
        var created = await ReadConsentAsync(await client.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" }));

        (await client.PostAsJsonAsync($"/api/v1/members/M1/consents/{created.Id}/activate",
            new JsonObject { ["activatedBy"] = "attacker" })).EnsureSuccessStatusCode();
        var revoked = await ReadConsentAsync(await client.PostAsJsonAsync(
            $"/api/v1/members/M1/consents/{created.Id}/revoke",
            new JsonObject { ["reasonCode"] = "MemberRequest", ["revokedBy"] = "attacker" }));

        revoked.ActivatedBy.Should().Be(TokenUser);
        revoked.RevokedBy.Should().Be(TokenUser);
        EventsFor(tenant).Should().HaveCount(3).And.OnlyContain(e => e.ActorId == TokenUser);
        _factory.Publisher.Calls.Where(c => c.TenantId == tenant)
            .Should().HaveCount(3).And.OnlyContain(c => c.Actor == TokenUser);
    }

    [Fact]
    public async Task Revoke_ClaimingExpired_Returns400()
    {
        var tenant = NewTenant();
        var client = NewClient(tenant);
        var created = await ReadConsentAsync(await client.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" }));
        (await client.PostAsync($"/api/v1/members/M1/consents/{created.Id}/activate", null)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync($"/api/v1/members/M1/consents/{created.Id}/revoke",
            new JsonObject { ["reasonCode"] = "Expired" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.Repo.GetByIdAsync(tenant, "M1", created.Id))!.Status.Should().Be(ConsentStatus.Active);
    }

    // ── Tenant from token ───────────────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/v1/members/M1/consents")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeader_WithoutToken_Returns401_AndDoesNotWrite()
    {
        var tenant = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);

        (await client.GetAsync("/api/v1/members/M1/consents")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a header naming a tenant is not authentication");
        (await client.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        EventsFor(tenant).Should().BeEmpty();
    }

    [Fact]
    public async Task TenantHeader_DisagreeingWithToken_Returns403_AndDoesNotWrite()
    {
        var victim = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken("tenant-attacker", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", victim);

        var response = await client.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        EventsFor(victim).Should().BeEmpty();
        EventsFor("tenant-attacker").Should().BeEmpty();
    }

    [Fact]
    public async Task TokenTenant_IsUsed_WhenNoHeaderIsSent()
    {
        var tenant = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(tenant, ChoRolePermissions.TenantAdmin));

        var created = await ReadConsentAsync(await client.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" }));

        created.TenantId.Should().Be(tenant);
        (await _factory.Repo.GetByIdAsync(tenant, "M1", created.Id)).Should().NotBeNull();
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoleWithoutConsentPermissions_Returns403()
    {
        var client = NewClient(NewTenant(), ChoRolePermissions.ClaimsExaminer); // no consent:*

        (await client.GetAsync("/api/v1/members/M1/consents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/members/M1/consents/authorization-snapshots")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButCannotGrantOrRevoke()
    {
        var tenant = NewTenant();
        var writer = NewClient(tenant, ChoRolePermissions.MemberServices); // consent:read + consent:write
        var created = await ReadConsentAsync(await writer.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" }));

        var reader = NewClient(tenant, ChoRolePermissions.UMCoordinator); // consent:read only
        (await reader.GetAsync("/api/v1/members/M1/consents")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PostAsJsonAsync("/api/v1/members/M1/consents",
            new JsonObject { ["consentType"] = "GeneralAuthorization" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "recording a consent requires consent:write");
        (await reader.PostAsync($"/api/v1/members/M1/consents/{created.Id}/activate", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"/api/v1/members/M1/consents/{created.Id}/revoke", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await _factory.Repo.GetByIdAsync(tenant, "M1", created.Id))!.Status.Should().Be(ConsentStatus.Draft);
        EventsFor(tenant).Should().ContainSingle();
    }

    [Fact]
    public async Task ServiceToken_CanReadAuthorizationSnapshots()
    {
        // fhir-service reads snapshots service-to-service (Payer-to-Payer,
        // Provider Access); with no user on the call it presents a service token.
        var tenant = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("fhir-service", tenant));

        var response = await client.GetAsync("/api/v1/members/M1/consents/authorization-snapshots");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ConsentAuthorizationSnapshotResponse>(Json))!
            .Items.Should().BeEmpty();
    }
}
