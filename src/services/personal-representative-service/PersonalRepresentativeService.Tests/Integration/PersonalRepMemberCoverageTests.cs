using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Tests.Integration;

/// <summary>
/// Every member a representative covers passes the activation controls. Members
/// are added only while the representative is a Draft, so nobody (creator,
/// second user, service, or a tenant with the second-person override) can
/// extend an active representative to a new member; future-dated associations
/// are checked at activation like current ones; taking a member away stays a
/// single-person action.
/// </summary>
public class PersonalRepMemberCoverageTests : IClassFixture<PersonalRepLifecycleSmokeTests.Factory>
{
    private const string Creator = "creator-1";
    private const string Reviewer = "reviewer-2";
    private const string Base = "/api/v1/personal-representatives";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PersonalRepLifecycleSmokeTests.Factory _factory;

    public PersonalRepMemberCoverageTests(PersonalRepLifecycleSmokeTests.Factory factory) => _factory = factory;

    private HttpClient User(string subject, string tenant)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient Service(string clientId, string tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }

    private static string NewTenant() => "tenant-cov-" + Guid.NewGuid().ToString("N")[..8];

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient client, string repId, string memberId, DateTime? effectiveFrom = null)
        => client.PostAsJsonAsync($"{Base}/{repId}/associations", new JsonObject
        {
            ["memberId"] = memberId,
            ["effectiveFrom"] = effectiveFrom?.ToString("o")
        });

    private async Task<string> DraftAsync(string tenant, PersonalRepCredentialType type, params string[] memberIds)
    {
        var creator = User(Creator, tenant);
        var created = await creator.PostAsJsonAsync(Base, new JsonObject { ["credentialType"] = type.ToString() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var repId = (await created.Content.ReadFromJsonAsync<PersonalRepresentative>(Json))!.Id;
        foreach (var memberId in memberIds)
            (await AddMemberAsync(creator, repId, memberId)).StatusCode.Should().Be(HttpStatusCode.Created);
        return repId;
    }

    /// <summary>A guardian of M1, established by the creator and activated by a reviewer with the order on file.</summary>
    private async Task<string> ActiveGuardianAsync(string tenant)
    {
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1", "M2"); // even an order naming M2 does not let M2 be added later
        (await User(Reviewer, tenant).PostAsJsonAsync($"{Base}/{repId}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = order })).StatusCode.Should().Be(HttpStatusCode.OK);
        return repId;
    }

    private async Task ShouldNotCoverAsync(string tenant, string repId, string memberId)
    {
        (await _factory.Repo.FindActiveAssociationAsync(tenant, repId, memberId)).Should().BeNull();
        (await _factory.Repo.ListAssociationsForMemberAsync(tenant, memberId)).Should().NotContain(a => a.RepId == repId);
        _factory.Publisher.AssociationCalls.Should().NotContain(c => c.PersonalRepId == repId && c.MemberId == memberId);
        _factory.Repo.SnapshotEvents().Should().NotContain(e => e.PersonalRepId == repId && e.MemberId == memberId);
    }

    // ── Adding a member to an active representative ─────────────────────

    [Theory]
    [InlineData(Creator)]
    [InlineData(Reviewer)]
    public async Task AddingAMember_ToAnActiveRepresentative_Returns409(string subject)
    {
        var tenant = NewTenant();
        var repId = await ActiveGuardianAsync(tenant);

        var response = await AddMemberAsync(User(subject, tenant), repId, "M2");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        problem.Title.Should().Be("Members can be added only to a Draft representative");
        problem.Detail.Should().Contain("establish a new representative");
        await ShouldNotCoverAsync(tenant, repId, "M2");
        _factory.Logs.Entries.Should().Contain(l =>
            l.EventId.Id == PersonalRepresentativesController.MemberAdditionRefusedAuditEvent.Id
            && l.Level == LogLevel.Warning
            && l.Message.StartsWith("AUDIT member addition refused")
            && l.Message.Contains(subject) && l.Message.Contains(repId) && l.Message.Contains("M2"));
    }

    [Fact]
    public async Task AddingAMember_ToAnActiveRepresentative_IsRefused_EvenWithTheTenantOverride()
    {
        var tenant = NewTenant();
        _factory.TenantService.SetRequireSecondPerson(tenant, false);
        var repId = await ActiveGuardianAsync(tenant);

        (await AddMemberAsync(User(Creator, tenant), repId, "M2")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await ShouldNotCoverAsync(tenant, repId, "M2");
    }

    [Fact]
    public async Task AddingAMember_ToAnActiveRepresentative_IsRefused_ForAServiceToken()
    {
        var tenant = NewTenant();
        var repId = await ActiveGuardianAsync(tenant);

        (await AddMemberAsync(Service("member-service", tenant), repId, "M2")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await ShouldNotCoverAsync(tenant, repId, "M2");
    }

    [Fact]
    public async Task AddingAMember_ToARevokedRepresentative_Returns409()
    {
        var tenant = NewTenant();
        var repId = await ActiveGuardianAsync(tenant);
        (await User(Creator, tenant).PostAsJsonAsync($"{Base}/{repId}/revoke",
            new JsonObject { ["reasonCode"] = "GuardianshipEnded" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AddMemberAsync(User(Reviewer, tenant), repId, "M2")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await ShouldNotCoverAsync(tenant, repId, "M2");
    }

    [Fact]
    public async Task ANewMember_IsCoveredByANewRepresentative_ThatASecondUserActivates()
    {
        var tenant = NewTenant();
        await ActiveGuardianAsync(tenant);
        var forM2 = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M2");
        var order = _factory.MemberDocuments.Add(tenant, "M2");

        (await User(Creator, tenant).PostAsJsonAsync($"{Base}/{forM2}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = order })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "the creator cannot activate the new representative alone");
        (await User(Reviewer, tenant).PostAsJsonAsync($"{Base}/{forM2}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = order })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Future-dated associations ───────────────────────────────────────

    [Fact]
    public async Task FutureDatedMember_MustBeLinkedToTheDocument_AtActivation()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        (await AddMemberAsync(User(Creator, tenant), repId, "M2", DateTime.UtcNow.AddDays(30)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var orderForM1Only = _factory.MemberDocuments.Add(tenant, "M1");

        var refused = await User(Reviewer, tenant).PostAsJsonAsync($"{Base}/{repId}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = orderForM1Only });

        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Detail.Should().Contain("not linked to member M2");
        (await _factory.Repo.GetByIdAsync(tenant, repId))!.Status.Should().Be(PersonalRepStatus.Draft);

        var orderForBoth = _factory.MemberDocuments.Add(tenant, "M1", "M2");
        (await User(Reviewer, tenant).PostAsJsonAsync($"{Base}/{repId}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = orderForBoth })).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repo.SnapshotEvents().Single(e =>
                e.PersonalRepId == repId && e.EventType == PersonalRepEventType.PersonalRepActivated)
            .Payload!["approvedMemberIds"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "M1", "M2" });
    }

    // ── Reducing access stays single-person ─────────────────────────────

    [Fact]
    public async Task Creator_CanRemoveAMember_FromAnActiveRepresentative()
    {
        var tenant = NewTenant();
        var repId = await ActiveGuardianAsync(tenant);

        var response = await User(Creator, tenant).DeleteAsync($"{Base}/{repId}/associations/M1");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _factory.Repo.FindActiveAssociationAsync(tenant, repId, "M1")).Should().BeNull();
    }
}
