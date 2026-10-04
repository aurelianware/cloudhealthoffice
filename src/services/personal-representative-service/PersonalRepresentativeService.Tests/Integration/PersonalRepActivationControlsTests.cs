using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Integration;

/// <summary>
/// Activation controls over the real Program pipeline, with member-document-service
/// and tenant-service faked behind the real HttpClients and the shared outbound
/// token handler:
/// <list type="bullet">
///   <item>guardians, healthcare powers of attorney and surrogates need a
///   proof-of-authority document in the same tenant, linked to the member;</item>
///   <item>member-document-service failures answer 503 and activate nothing;</item>
///   <item>the creator cannot activate (403 "Separation of duties") unless the
///   tenant sets <c>personalRepresentativeControls.requireSecondPerson = false</c>,
///   which is audit-logged; a missing setting or unreachable tenant-service keeps
///   the rule on;</item>
///   <item>a service token cannot activate.</item>
/// </list>
/// </summary>
public class PersonalRepActivationControlsTests : IClassFixture<PersonalRepLifecycleSmokeTests.Factory>
{
    private const string Creator = "creator-1";
    private const string Reviewer = "reviewer-2";
    private const string Base = "/api/v1/personal-representatives";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PersonalRepLifecycleSmokeTests.Factory _factory;

    public PersonalRepActivationControlsTests(PersonalRepLifecycleSmokeTests.Factory factory) => _factory = factory;

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

    private static string NewTenant() => "tenant-act-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A Draft rep of <paramref name="type"/>, established by <see cref="Creator"/> and associated with the members.</summary>
    private async Task<string> DraftAsync(string tenant, PersonalRepCredentialType type, params string[] memberIds)
    {
        var creator = User(Creator, tenant);
        var created = await creator.PostAsJsonAsync(Base,
            new JsonObject { ["credentialType"] = type.ToString(), ["firstName"] = "Pat", ["lastName"] = "Rep" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rep = (await created.Content.ReadFromJsonAsync<PersonalRepresentative>(Json))!;
        foreach (var memberId in memberIds)
        {
            (await creator.PostAsJsonAsync($"{Base}/{rep.Id}/associations",
                new JsonObject { ["memberId"] = memberId })).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        return rep.Id;
    }

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, string repId, string? documentId = null)
        => client.PostAsJsonAsync($"{Base}/{repId}/activate",
            new JsonObject { ["proofOfAuthorityDocumentId"] = documentId });

    private static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;

    private async Task ShouldStayDraftAsync(string tenant, string repId)
    {
        var stored = (await _factory.Repo.GetByIdAsync(tenant, repId))!;
        stored.Status.Should().Be(PersonalRepStatus.Draft);
        stored.ActivatedBy.Should().BeNull();
        stored.ProofOfAuthorityVerifiedBy.Should().BeNull();
        _factory.Repo.SnapshotEvents().Should().NotContain(e =>
            e.PersonalRepId == repId && e.EventType == PersonalRepEventType.PersonalRepActivated);
        _factory.Publisher.StatusCalls.Should().NotContain(c =>
            c.PersonalRepId == repId && c.ToStatus == PersonalRepStatus.Active);
    }

    // ── Proof of authority ──────────────────────────────────────────────

    [Theory]
    [InlineData(PersonalRepCredentialType.LegalGuardian)]
    [InlineData(PersonalRepCredentialType.HealthcarePowerOfAttorney)]
    [InlineData(PersonalRepCredentialType.HealthcareSurrogate)]
    public async Task DocumentTypes_WithoutDocument_Return400_AndStayDraft(PersonalRepCredentialType type)
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, type, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ProblemAsync(response);
        problem.Title.Should().Be("Proof of authority required");
        problem.Detail.Should().Contain(type.ToString()).And.Contain("proofOfAuthorityDocumentId");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Theory]
    [InlineData(PersonalRepCredentialType.Parent)]
    [InlineData(PersonalRepCredentialType.Conservator)]
    [InlineData(PersonalRepCredentialType.Other)]
    public async Task OtherTypes_WithoutDocument_AreActivatedByASecondUser(PersonalRepCredentialType type)
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, type, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = (await _factory.Repo.GetByIdAsync(tenant, repId))!;
        stored.Status.Should().Be(PersonalRepStatus.Active);
        stored.ActivatedBy.Should().Be(Reviewer);
        stored.ProofOfAuthorityVerifiedBy.Should().BeNull();
        _factory.MemberDocuments.Requests.Should().NotContain(r => r.TenantHeader == tenant,
            "no document is looked up for a type that needs none");
    }

    [Fact]
    public async Task VerifiedDocument_SecondUser_Activates_AndRecordsWhoVerifiedIt()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1", "M2");
        var order = _factory.MemberDocuments.Add(tenant, "M1", "M2");
        var before = DateTime.UtcNow;

        var response = await ActivateAsync(User(Reviewer, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = (await response.Content.ReadFromJsonAsync<PersonalRepresentative>(Json))!;
        view.ProofOfAuthorityDocumentId.Should().Be(order);
        view.ProofOfAuthorityVerifiedBy.Should().Be(Reviewer);

        var stored = (await _factory.Repo.GetByIdAsync(tenant, repId))!;
        stored.Status.Should().Be(PersonalRepStatus.Active);
        stored.ActivatedBy.Should().Be(Reviewer);
        stored.ProofOfAuthorityDocumentId.Should().Be(order);
        stored.ProofOfAuthorityVerifiedBy.Should().Be(Reviewer);
        stored.ProofOfAuthorityVerifiedAt.Should().NotBeNull().And.BeOnOrAfter(before);

        // Audit event: who verified which document, and that no override was used.
        var activated = _factory.Repo.SnapshotEvents().Single(e =>
            e.PersonalRepId == repId && e.EventType == PersonalRepEventType.PersonalRepActivated);
        activated.ActorId.Should().Be(Reviewer);
        activated.Payload!["proofOfAuthorityDocumentId"]!.GetValue<string>().Should().Be(order);
        activated.Payload["proofOfAuthorityVerifiedBy"]!.GetValue<string>().Should().Be(Reviewer);
        activated.Payload["proofOfAuthorityVerifiedAt"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        activated.Payload["createdBy"]!.GetValue<string>().Should().Be(Creator);
        activated.Payload["secondPersonOverride"]!.GetValue<bool>().Should().BeFalse();

        // Kafka: verifier and time, no document id or content.
        var published = _factory.Publisher.StatusCalls.Single(c =>
            c.PersonalRepId == repId && c.ToStatus == PersonalRepStatus.Active);
        published.Event!.ProofOfAuthorityVerifiedBy.Should().Be(Reviewer);
        published.Event.ProofOfAuthorityVerifiedAt.Should().Be(stored.ProofOfAuthorityVerifiedAt);
        JsonSerializer.Serialize(published.Event, Json).Should().NotContain(order);

        // The lookup went to member-document-service with the reviewer's own
        // token and the token tenant.
        var lookup = _factory.MemberDocuments.Requests.Single(r => r.TenantHeader == tenant);
        lookup.Host.Should().Be("member-document-service");
        lookup.Path.Should().Be($"/api/v1/member-documents/{order}");
        lookup.Authorization.Should().StartWith("Bearer ");
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(lookup.Authorization!["Bearer ".Length..]).Subject.Should().Be(Reviewer);
    }

    [Fact]
    public async Task DocumentIdRecordedAtCreation_IsUsedWhenActivateNamesNone()
    {
        var tenant = NewTenant();
        var order = _factory.MemberDocuments.Add(tenant, "M1");
        var creator = User(Creator, tenant);
        var created = await creator.PostAsJsonAsync(Base, new JsonObject
        {
            ["credentialType"] = "HealthcareSurrogate",
            ["proofOfAuthorityDocumentId"] = order
        });
        var repId = (await created.Content.ReadFromJsonAsync<PersonalRepresentative>(Json))!.Id;
        (await creator.PostAsJsonAsync($"{Base}/{repId}/associations",
            new JsonObject { ["memberId"] = "M1" })).EnsureSuccessStatusCode();

        var response = await User(Reviewer, tenant).PostAsync($"{Base}/{repId}/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.Repo.GetByIdAsync(tenant, repId))!.ProofOfAuthorityVerifiedBy.Should().Be(Reviewer);
    }

    [Fact]
    public async Task UnknownDocument_Returns422()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).Detail.Should().Contain("not found in this tenant");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task OtherTenantsDocument_Returns422()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.HealthcarePowerOfAttorney, "M1");
        var otherTenantsOrder = _factory.MemberDocuments.Add(otherTenant, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId, otherTenantsOrder);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _factory.MemberDocuments.Requests.Should().Contain(r => r.TenantHeader == tenant)
            .And.NotContain(r => r.TenantHeader == otherTenant, "the lookup names the caller's tenant only");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task DocumentBodyNamingAnotherTenant_Returns422()
    {
        // Defense in depth: even if member-document-service answered with a
        // document from another tenant, the tenant on the document decides.
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var leaked = _factory.MemberDocuments.Add("tenant-elsewhere", "M1");
        _factory.MemberDocuments.AddRaw(tenant, leaked, _factory.MemberDocuments.Get("tenant-elsewhere", leaked));

        var response = await ActivateAsync(User(Reviewer, tenant), repId, leaked);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).Detail.Should().Contain("does not belong to this tenant");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task OtherMembersDocument_Returns422()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var otherMembersOrder = _factory.MemberDocuments.Add(tenant, "M2");

        var response = await ActivateAsync(User(Reviewer, tenant), repId, otherMembersOrder);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).Detail.Should().Contain("not linked to member M1");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task DocumentCoveringOnlySomeAssociatedMembers_Returns422()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1", "M2");
        var orderForM1Only = _factory.MemberDocuments.Add(tenant, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId, orderForM1Only);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).Detail.Should().Contain("not linked to member M2");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task UnfinalizedUpload_Returns422()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1");
        _factory.MemberDocuments.Get(tenant, order)["pendingUploadBlobPath"] = $"{tenant}/staging/{order}";

        var response = await ActivateAsync(User(Reviewer, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task NoAssociatedMember_Returns422()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian);
        var order = _factory.MemberDocuments.Add(tenant, "M1");

        var response = await ActivateAsync(User(Reviewer, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(response)).Detail.Should().Contain("Associate the representative");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberDocumentServiceFailure_Returns503_AndActivatesNothing(bool unreachable)
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1");
        if (unreachable) _factory.MemberDocuments.UnreachableTenants[tenant] = true;
        else _factory.MemberDocuments.FailingTenants[tenant] = true;

        var response = await ActivateAsync(User(Reviewer, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemAsync(response)).Detail.Should().Contain("not activated");
        await ShouldStayDraftAsync(tenant, repId);
    }

    // ── Second person ───────────────────────────────────────────────────

    [Fact]
    public async Task Creator_CannotActivate_Returns403SeparationOfDuties()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.Parent, "M1");

        var response = await ActivateAsync(User(Creator, tenant), repId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await ProblemAsync(response);
        problem.Title.Should().Be("Separation of duties");
        problem.Detail.Should().Contain("cannot activate");
        await ShouldStayDraftAsync(tenant, repId);
        _factory.TenantService.Requests.Should().Contain(r => r.TenantHeader == tenant && r.Authorization != null);

        // A second user can.
        (await ActivateAsync(User(Reviewer, tenant), repId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creator_IsRefused_BeforeTheDocumentIsLookedUp()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1");

        var response = await ActivateAsync(User(Creator, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.MemberDocuments.Requests.Should().NotContain(r => r.TenantHeader == tenant);
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task TenantOverride_AllowsCreatorToActivate_AndIsAuditLogged()
    {
        var tenant = NewTenant();
        _factory.TenantService.SetRequireSecondPerson(tenant, false);
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1");

        var response = await ActivateAsync(User(Creator, tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = (await _factory.Repo.GetByIdAsync(tenant, repId))!;
        stored.ActivatedBy.Should().Be(Creator);
        stored.ProofOfAuthorityVerifiedBy.Should().Be(Creator);

        _factory.Logs.Entries.Should().ContainSingle(l =>
                l.EventId.Id == PersonalRepActivationControls.SecondPersonOverrideAuditEvent.Id
                && l.Message.Contains(repId))
            .Which.Should().Match<Fakes.CapturedLog>(l =>
                l.Level == LogLevel.Warning
                && l.Message.StartsWith("AUDIT separation-of-duties override")
                && l.Message.Contains(Creator)
                && l.Message.Contains(tenant));
        _factory.Repo.SnapshotEvents().Single(e =>
                e.PersonalRepId == repId && e.EventType == PersonalRepEventType.PersonalRepActivated)
            .Payload!["secondPersonOverride"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TenantOverride_IsNotLogged_WhenASecondUserActivates()
    {
        var tenant = NewTenant();
        _factory.TenantService.SetRequireSecondPerson(tenant, false);
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.Parent, "M1");

        (await ActivateAsync(User(Reviewer, tenant), repId)).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Logs.Entries.Should().NotContain(l =>
            l.EventId.Id == PersonalRepActivationControls.SecondPersonOverrideAuditEvent.Id && l.Message.Contains(repId));
    }

    [Theory]
    [InlineData("missing-block")]
    [InlineData("explicit-true")]
    [InlineData("tenant-service-down")]
    public async Task SecondPersonRule_FailsClosed(string setting)
    {
        var tenant = NewTenant();
        switch (setting)
        {
            case "missing-block": _factory.TenantService.SetRequireSecondPerson(tenant, null); break;
            case "explicit-true": _factory.TenantService.SetRequireSecondPerson(tenant, true); break;
            default: _factory.TenantService.FailingTenants[tenant] = true; break;
        }
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.Parent, "M1");

        var response = await ActivateAsync(User(Creator, tenant), repId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemAsync(response)).Title.Should().Be("Separation of duties");
        await ShouldStayDraftAsync(tenant, repId);
    }

    [Fact]
    public async Task ServiceToken_CannotActivate_EvenWithOverrideAndDocument()
    {
        var tenant = NewTenant();
        _factory.TenantService.SetRequireSecondPerson(tenant, false);
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.LegalGuardian, "M1");
        var order = _factory.MemberDocuments.Add(tenant, "M1");

        var response = await ActivateAsync(Service("consent-service", tenant), repId, order);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemAsync(response)).Detail.Should().Contain("Service tokens cannot activate");
        await ShouldStayDraftAsync(tenant, repId);
        _factory.MemberDocuments.Requests.Should().NotContain(r => r.TenantHeader == tenant);
    }

    [Fact]
    public async Task ServiceToken_CannotActivate_ParentWithoutDocument()
    {
        var tenant = NewTenant();
        var repId = await DraftAsync(tenant, PersonalRepCredentialType.Parent, "M1");

        var response = await ActivateAsync(Service("member-service", tenant), repId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ShouldStayDraftAsync(tenant, repId);
    }
}
