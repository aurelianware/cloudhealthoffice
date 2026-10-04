using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Tests.Integration;

/// <summary>
/// Pins the shared CHO authentication contract for
/// personal-representative-service over the real Program pipeline. Who may
/// act for a member (§164.502(g)) decides who reads that member's PHI and
/// grants consent, so: the tenant and the acting user come from the validated
/// token only; establishing, activating, associating and revoking need
/// members:write; and the "active representatives of member Y" resolver admits
/// only the named service clients.
/// </summary>
public class PersonalRepAuthenticationTests : IClassFixture<PersonalRepLifecycleSmokeTests.Factory>
{
    private const string TokenUser = "token-user-42";
    private const string Base = "/api/v1/personal-representatives";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PersonalRepLifecycleSmokeTests.Factory _factory;

    public PersonalRepAuthenticationTests(PersonalRepLifecycleSmokeTests.Factory factory)
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

    private HttpClient NewServiceClient(string clientId, string tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }

    private static string NewTenant() => "tenant-auth-" + Guid.NewGuid().ToString("N")[..8];

    private static JsonObject GuardianBody() => new()
    {
        ["credentialType"] = "LegalGuardian",
        ["firstName"] = "Gina",
        ["lastName"] = "Guardian",
    };

    private static async Task<PersonalRepresentative> ReadRepAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"request should succeed, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<PersonalRepresentative>(body, Json)!;
    }

    private IEnumerable<PersonalRepEvent> EventsFor(string tenant)
        => _factory.Repo.SnapshotEvents().Where(e => e.TenantId == tenant);

    /// <summary>An Active representative of <paramref name="memberId"/> in <paramref name="tenant"/>.</summary>
    private async Task<PersonalRepresentative> ActiveRepForAsync(string tenant, string memberId)
    {
        var admin = NewClient(tenant);
        var rep = await ReadRepAsync(await admin.PostAsJsonAsync(Base, GuardianBody()));
        (await admin.PostAsJsonAsync($"{Base}/{rep.Id}/associations",
            new JsonObject { ["memberId"] = memberId })).EnsureSuccessStatusCode();

        // A second user activates, with the guardianship order on file.
        var reviewer = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("reviewer-7", ChoRolePermissions.TenantAdmin));
        reviewer.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        var order = _factory.MemberDocuments.Add(tenant, memberId);
        (await reviewer.PostAsJsonAsync($"{Base}/{rep.Id}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = order })).EnsureSuccessStatusCode();
        return rep;
    }

    // ── Actor from token ────────────────────────────────────────────────

    [Fact]
    public async Task Writes_RecordTokenSubject_IgnoringBodyActorStatusAndTenant()
    {
        var tenant = NewTenant();
        var client = NewClient(tenant);
        var forged = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // One user does every write here, so the tenant has turned the
        // second-person rule off; the guardianship order is on file.
        _factory.TenantService.SetRequireSecondPerson(tenant, false);
        var order = _factory.MemberDocuments.Add(tenant, "M1");

        var body = GuardianBody();
        body["createdBy"] = "attacker";
        body["activatedBy"] = "attacker";
        body["activatedAt"] = forged.ToString("o");
        body["status"] = "Active";
        body["createdAt"] = forged.ToString("o");
        body["tenantId"] = "tenant-victim";
        var created = await ReadRepAsync(await client.PostAsJsonAsync(Base, body));

        var stored = (await _factory.Repo.GetByIdAsync(tenant, created.Id))!;
        stored.CreatedBy.Should().Be(TokenUser);
        stored.Status.Should().Be(PersonalRepStatus.Draft);
        stored.TenantId.Should().Be(tenant);
        stored.CreatedAt.Should().BeAfter(forged.AddYears(1));
        stored.ActivatedBy.Should().BeNull();
        (await _factory.Repo.GetByIdAsync("tenant-victim", created.Id)).Should().BeNull();

        (await client.PostAsJsonAsync($"{Base}/{created.Id}/associations",
            new JsonObject { ["memberId"] = "M1", ["createdBy"] = "attacker" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"{Base}/{created.Id}/activate",
            new JsonObject { ["activatedBy"] = "attacker", ["proofOfAuthorityDocumentId"] = order })).EnsureSuccessStatusCode();
        (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Base}/{created.Id}/associations/M1")
        {
            Content = JsonContent.Create(new JsonObject { ["updatedBy"] = "attacker" })
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var revoked = await ReadRepAsync(await client.PostAsJsonAsync($"{Base}/{created.Id}/revoke",
            new JsonObject { ["reasonCode"] = "PoaRevoked", ["inactivatedBy"] = "attacker" }));

        revoked.ActivatedBy.Should().Be(TokenUser);
        revoked.InactivatedBy.Should().Be(TokenUser);
        var associations = _factory.Repo.SnapshotAssociations().Where(a => a.TenantId == tenant && a.RepId == created.Id);
        associations.Should().HaveCount(2).And.OnlyContain(a => a.CreatedBy == TokenUser && a.UpdatedBy == TokenUser);
        EventsFor(tenant).Should().HaveCount(5).And.OnlyContain(e => e.ActorId == TokenUser);
        _factory.Publisher.StatusCalls.Where(c => c.TenantId == tenant)
            .Should().HaveCount(3).And.OnlyContain(c => c.Actor == TokenUser);
        _factory.Publisher.AssociationCalls.Where(c => c.TenantId == tenant)
            .Should().HaveCount(2).And.OnlyContain(c => c.Actor == TokenUser);
    }

    [Fact]
    public async Task Revoke_ClaimingExpired_Returns400_AndRepStaysActive()
    {
        var tenant = NewTenant();
        var rep = await ActiveRepForAsync(tenant, "M1");

        var response = await NewClient(tenant).PostAsJsonAsync($"{Base}/{rep.Id}/revoke",
            new JsonObject { ["reasonCode"] = "Expired" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _factory.Repo.GetByIdAsync(tenant, rep.Id))!.Status.Should().Be(PersonalRepStatus.Active);
    }

    // ── Tenant from token ───────────────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/v1/members/M1/personal-representatives")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/members/M1/personal-representatives/active")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeader_WithoutToken_Returns401_AndDoesNotWrite()
    {
        var tenant = NewTenant();
        var rep = await ActiveRepForAsync(tenant, "M1");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);

        (await client.GetAsync($"{Base}/{rep.Id}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a header naming a tenant is not authentication");
        (await client.GetAsync("/api/v1/members/M1/personal-representatives/active")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(Base, GuardianBody())).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync($"{Base}/{rep.Id}/associations",
            new JsonObject { ["memberId"] = "M-victim" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"{Base}/{rep.Id}/revoke", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await _factory.Repo.GetByIdAsync(tenant, rep.Id))!.Status.Should().Be(PersonalRepStatus.Active);
        EventsFor(tenant).Should().HaveCount(3, "only the setup's create, associate and activate");
    }

    [Fact]
    public async Task TenantHeader_DisagreeingWithToken_Returns403_AndDoesNotWrite()
    {
        var victim = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken("tenant-attacker", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", victim);

        var response = await client.PostAsJsonAsync(Base, GuardianBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        EventsFor(victim).Should().BeEmpty();
        EventsFor("tenant-attacker").Should().BeEmpty();
    }

    [Fact]
    public async Task TokenTenant_IsUsed_WhenNoHeaderIsSent_AndOtherTenantsCannotSeeTheRep()
    {
        var tenant = NewTenant();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(tenant, ChoRolePermissions.TenantAdmin));

        var created = await ReadRepAsync(await client.PostAsJsonAsync(Base, GuardianBody()));

        created.TenantId.Should().Be(tenant);
        (await _factory.Repo.GetByIdAsync(tenant, created.Id)).Should().NotBeNull();
        (await NewClient(NewTenant()).GetAsync($"{Base}/{created.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoleWithoutMembersPermissions_Returns403()
    {
        var tenant = NewTenant();
        var rep = await ActiveRepForAsync(tenant, "M1");
        var client = NewClient(tenant, ChoRolePermissions.Finance); // no members:*

        (await client.GetAsync($"{Base}/{rep.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"{Base}/{rep.Id}/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/members/M1/personal-representatives")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButCannotEstablishAssociateActivateOrRevoke()
    {
        var tenant = NewTenant();
        var admin = NewClient(tenant);
        var draft = await ReadRepAsync(await admin.PostAsJsonAsync(Base, GuardianBody()));
        var active = await ActiveRepForAsync(tenant, "M1");
        var eventsBefore = EventsFor(tenant).Count();

        var reader = NewClient(tenant, ChoRolePermissions.MemberServices); // members:read, no members:write
        (await reader.GetAsync($"{Base}/{active.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"{Base}/{active.Id}/history")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync("/api/v1/members/M1/personal-representatives")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await reader.PostAsJsonAsync(Base, GuardianBody())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "establishing a representative requires members:write");
        (await reader.PostAsJsonAsync($"{Base}/{draft.Id}/associations",
            new JsonObject { ["memberId"] = "M2" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"{Base}/{draft.Id}/activate", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"{Base}/{active.Id}/associations/M1")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"{Base}/{active.Id}/revoke", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await _factory.Repo.GetByIdAsync(tenant, draft.Id))!.Status.Should().Be(PersonalRepStatus.Draft);
        (await _factory.Repo.GetByIdAsync(tenant, active.Id))!.Status.Should().Be(PersonalRepStatus.Active);
        EventsFor(tenant).Should().HaveCount(eventsBefore);
    }

    // ── Resolver: named service clients only ────────────────────────────

    [Theory]
    [InlineData("consent-service")]
    [InlineData("appeals-service")]
    [InlineData("fhir-service")]
    public async Task Resolver_NamedServiceClient_ResolvesInItsTokenTenantOnly(string clientId)
    {
        var tenant = NewTenant();
        var rep = await ActiveRepForAsync(tenant, "M1");
        var otherTenant = NewTenant();
        await ActiveRepForAsync(otherTenant, "M1");

        var response = await NewServiceClient(clientId, tenant)
            .GetAsync("/api/v1/members/M1/personal-representatives/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<MemberRepresentativesResponse>(Json))!;
        body.Items.Should().ContainSingle().Which.PersonalRepId.Should().Be(rep.Id);
    }

    [Fact]
    public async Task Resolver_UserTokens_AndOtherServices_Return403()
    {
        var tenant = NewTenant();
        await ActiveRepForAsync(tenant, "M1");
        const string path = "/api/v1/members/M1/personal-representatives/active";

        (await NewClient(tenant).GetAsync(path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a user token, even TenantAdmin, is not a resolver client");
        (await NewClient(tenant, ChoRolePermissions.MemberServices).GetAsync(path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await NewServiceClient("claims-service", tenant).GetAsync(path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "service tokens satisfy members:read, so the resolver names its callers");
    }
}
