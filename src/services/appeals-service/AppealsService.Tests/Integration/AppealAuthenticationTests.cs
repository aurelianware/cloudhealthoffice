using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppealsService.Models;
using CloudHealthOffice.Infrastructure.Security;

namespace AppealsService.Tests.Integration;

/// <summary>
/// Pins the shared CHO authentication contract for appeals-service:
/// the tenant and the acting user come from the validated token only.
/// Request bodies that name an actor ("attacker") and headers that name a
/// different tenant are ignored or rejected.
/// </summary>
public class AppealAuthenticationTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string TokenUser = "token-user-42";
    private const string Tenant = "tenant-auth";

    private readonly AppealsWebApplicationFactory _factory;

    public AppealAuthenticationTests(AppealsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient NewClient(string tenant = Tenant, string subject = TokenUser, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonObject CreateBody(string claimId = "CLM-AUTH-001") => new()
    {
        ["claimId"] = claimId,
        ["claimNumber"] = "CLM-0001",
        ["memberId"] = "M-0001",
        ["patientName"] = "Jane Doe",
        ["providerNPI"] = "1234567890",
        ["appealReason"] = "Denied service was medically necessary.",
        ["lineOfBusiness"] = "commercial",
        ["appealType"] = "reconsideration",
        ["appealLevel"] = "firstLevel",
        ["appealedAmount"] = 100m,
        ["deniedAmount"] = 100m,
    };

    private static async Task<Appeal> ReadAppealAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"request should succeed, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Appeal>(body, JsonOptions)!;
    }

    private async Task<Appeal> CreateAsync(HttpClient client, JsonObject? body = null)
        => await ReadAppealAsync(await client.PostAsJsonAsync("/api/appeals", body ?? CreateBody()));

    private async Task PostOkAsync(HttpClient client, string url, object body)
        => (await client.PostAsJsonAsync(url, body)).EnsureSuccessStatusCode();

    // ── Actor from token ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAppeal_BodySubmittedBy_IsIgnored_ActorComesFromToken()
    {
        _factory.Reset();
        var client = NewClient();

        var body = CreateBody();
        body["submittedBy"] = "attacker";
        body["createdBy"] = "attacker";
        var appeal = await CreateAsync(client, body);

        var stored = _factory.Repo.PeekStored(Tenant, appeal.Id)!;
        stored.SubmittedBy.Should().Be(TokenUser);
        stored.CreatedBy.Should().Be(TokenUser);
        _factory.Repo.SnapshotEvents()
            .Single(e => e.AppealId == appeal.Id && e.EventType == AppealEventType.AppealCreated)
            .ActorId.Should().Be(TokenUser);
        _factory.Publisher.Created.Should().ContainSingle(c => c.AppealId == appeal.Id && c.Actor == TokenUser);
    }

    [Fact]
    public async Task AddNote_BodyCreatedBy_IsIgnored_ActorComesFromToken()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);

        await PostOkAsync(client, $"/api/appeals/{appeal.Id}/notes", new JsonObject
        {
            ["noteText"] = "Spoof attempt.",
            ["createdBy"] = "attacker",
            ["isInternal"] = true,
        });

        var stored = _factory.Repo.PeekStored(Tenant, appeal.Id)!;
        stored.Notes.Should().ContainSingle().Which.CreatedBy.Should().Be(TokenUser);
        var noteEvent = _factory.Repo.SnapshotEvents()
            .Single(e => e.AppealId == appeal.Id && e.EventType == AppealEventType.AppealNoteAdded);
        noteEvent.ActorId.Should().Be(TokenUser);
        noteEvent.Payload!["author"]!.GetValue<string>().Should().Be(TokenUser);
    }

    [Fact]
    public async Task Close_BodyDecisionMaker_IsIgnored_ActorComesFromToken()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);
        await PostOkAsync(client, $"/api/appeals/{appeal.Id}/submit", new JsonObject());
        await PostOkAsync(client, $"/api/appeals/{appeal.Id}/begin-review", new JsonObject());

        await PostOkAsync(client, $"/api/appeals/{appeal.Id}/close", new JsonObject
        {
            ["closureReasonCode"] = "approved",
            ["decision"] = new JsonObject
            {
                ["decisionType"] = "approved",
                ["approvedAmount"] = 100m,
                ["decisionMaker"] = "attacker",
            },
        });

        var stored = _factory.Repo.PeekStored(Tenant, appeal.Id)!;
        stored.Decision!.DecisionMaker.Should().Be(TokenUser);
        stored.ClosedBy.Should().Be(TokenUser);
        stored.UpdatedBy.Should().Be(TokenUser);
        _factory.Repo.SnapshotEvents()
            .Where(e => e.AppealId == appeal.Id)
            .Should().OnlyContain(e => e.ActorId == TokenUser);
    }

    [Fact]
    public async Task AssignReviewer_RecordsTokenActor_OnAuditAndReassignmentNote()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);

        await PostOkAsync(client, $"/api/appeals/{appeal.Id}/assign", new JsonObject
        {
            ["assignedReviewerId"] = "reviewer-7",
            ["reassignmentReason"] = "Load balancing.",
            ["assignedBy"] = "attacker",
        });

        var stored = _factory.Repo.PeekStored(Tenant, appeal.Id)!;
        stored.AssignedReviewerId.Should().Be("reviewer-7");
        stored.Notes.Should().ContainSingle().Which.CreatedBy.Should().Be(TokenUser);
        _factory.Repo.SnapshotEvents()
            .Single(e => e.AppealId == appeal.Id && e.EventType == AppealEventType.AppealAssigned)
            .ActorId.Should().Be(TokenUser);
    }

    // ── Tenant from token ───────────────────────────────────────────────

    [Fact]
    public async Task TenantHeader_WithoutToken_Returns401()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/appeals/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a header naming a tenant is not authentication");
    }

    [Fact]
    public async Task TenantHeader_DisagreeingWithToken_Returns403_AndDoesNotWrite()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken("tenant-victim-owner", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-victim");

        var response = await client.PostAsJsonAsync("/api/appeals", CreateBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repo.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task TokenTenant_IsUsed_WhenNoHeaderIsSent()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken("tenant-from-token", ChoRolePermissions.TenantAdmin));

        var appeal = await CreateAsync(client);

        appeal.TenantId.Should().Be("tenant-from-token");
        _factory.Repo.PeekStored("tenant-from-token", appeal.Id).Should().NotBeNull();
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButCannotWrite()
    {
        _factory.Reset();
        var client = NewClient(roles: ChoRolePermissions.ComplianceOfficer); // *:read only

        (await client.GetAsync("/api/appeals/summary")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/appeals", CreateBody())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "creating an appeal requires appeals:write");
        _factory.Repo.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task RoleWithoutAppealsPermissions_CannotRead()
    {
        _factory.Reset();
        var client = NewClient(roles: ChoRolePermissions.ClaimsExaminer); // no appeals:*

        (await client.GetAsync("/api/appeals/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AppealsRole_CanWrite()
    {
        _factory.Reset();
        var client = NewClient(roles: ChoRolePermissions.UMCoordinator); // appeals:read + appeals:write

        var appeal = await CreateAsync(client);
        _factory.Repo.PeekStored(Tenant, appeal.Id)!.CreatedBy.Should().Be(TokenUser);
    }
}
