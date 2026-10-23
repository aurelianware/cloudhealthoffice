using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using WorkManagementService.Models;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests.Security;

/// <summary>
/// The HTTP surface over the real pipeline: a CHO token on every call, the
/// tenant and actor from the token, and the work-management permission on
/// every endpoint (workqueue:work to pull, :assign to push, :admin to
/// configure, :read for queue counts and item detail).
/// </summary>
[Collection(nameof(WorkServiceCollection))]
public class WorkApiTests
{
    private const string Examiner = ChoRolePermissions.ClaimsExaminer;
    private const string Supervisor = ChoRolePermissions.ClaimsSupervisor;
    private const string Admin = ChoRolePermissions.TenantAdmin;

    private readonly WorkServiceFactory _factory;

    public WorkApiTests(WorkServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private async Task PendedClaim(string claimId, string code = "NCCI", decimal amount = 100m)
    {
        _factory.Claims.Pended.Add(new CloudHealthOffice.Events.ClaimWorkEvent
        {
            EventType = CloudHealthOffice.Events.ClaimWorkEventTopics.WorkNeeded,
            ClaimId = claimId, ClaimNumber = "CN-" + claimId, PrimaryPendCode = code, PendCodes = [code],
            PendFingerprint = "fp-" + claimId, TotalChargeAmount = amount, ReceivedDate = DateTime.UtcNow.AddDays(-3),
        });
        _factory.Claims.ObservedAt = DateTime.UtcNow;
        var reconcile = await _factory.Client("admin", Admin).PostAsync("/api/v1/work/admin/reconcile", null);
        reconcile.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task Join(string user, string queueKey, decimal? limit = null)
    {
        var response = await _factory.Client("sue", Supervisor).PostAsJsonAsync("/api/v1/work/admin/memberships", new
        {
            userId = user, queueId = WorkTestKit.QueueId(queueKey), rank = 1, authorityLimit = limit,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Permissions: every endpoint ──────────────────────────────────────

    public static IEnumerable<object[]> Endpoints() =>
    [
        ["POST", "/api/v1/work/next", ChoRolePermissions.WorkQueueWork],
        ["GET", "/api/v1/work/mine", ChoRolePermissions.WorkQueueWork],
        ["POST", "/api/v1/work/x/release", ChoRolePermissions.WorkQueueWork],
        ["POST", "/api/v1/work/x/renew", ChoRolePermissions.WorkQueueWork],
        ["GET", "/api/v1/work", ChoRolePermissions.WorkQueueAssign],
        ["POST", "/api/v1/work/x/assign", ChoRolePermissions.WorkQueueAssign],
        ["POST", "/api/v1/work/x/unassign", ChoRolePermissions.WorkQueueAssign],
        ["GET", "/api/v1/work/assignees", ChoRolePermissions.WorkQueueAssign],
        ["GET", "/api/v1/work/queues", ChoRolePermissions.WorkQueueRead],
        ["GET", "/api/v1/work/x", ChoRolePermissions.WorkQueueRead],
        ["GET", "/api/v1/work/admin/queues", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/queues", ChoRolePermissions.WorkQueueAdmin],
        ["PUT", "/api/v1/work/admin/queues/x", ChoRolePermissions.WorkQueueAdmin],
        ["DELETE", "/api/v1/work/admin/queues/x", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/queues/reorder", ChoRolePermissions.WorkQueueAdmin],
        ["GET", "/api/v1/work/admin/teams", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/teams", ChoRolePermissions.WorkQueueAdmin],
        ["PUT", "/api/v1/work/admin/teams/x", ChoRolePermissions.WorkQueueAdmin],
        ["DELETE", "/api/v1/work/admin/teams/x", ChoRolePermissions.WorkQueueAdmin],
        ["GET", "/api/v1/work/admin/memberships", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/memberships", ChoRolePermissions.WorkQueueAdmin],
        ["PUT", "/api/v1/work/admin/memberships/x", ChoRolePermissions.WorkQueueAdmin],
        ["DELETE", "/api/v1/work/admin/memberships/x", ChoRolePermissions.WorkQueueAdmin],
        ["GET", "/api/v1/work/admin/users/alice/queues", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/recompute", ChoRolePermissions.WorkQueueAdmin],
        ["POST", "/api/v1/work/admin/reconcile", ChoRolePermissions.WorkQueueAdmin],
    ];

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null,
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpoint_RequiresAToken(string method, string path, string _)
    {
        var response = await _factory.CreateClient().SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpoint_RefusesARoleWithoutItsPermission(string method, string path, string permission)
    {
        // The narrowest built-in role still holding a work-management permission
        // below the required one (or none at all).
        var role = permission switch
        {
            ChoRolePermissions.WorkQueueRead => ChoRolePermissions.FinanceApprover,  // no workqueue:* at all
            ChoRolePermissions.WorkQueueWork => ChoRolePermissions.ComplianceOfficer, // *:read only
            ChoRolePermissions.WorkQueueAssign => Examiner,                           // read + work
            _ => Examiner,                                                            // admin: not an examiner's
        };

        var response = await _factory.Client("someone", role).SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpoint_AdmitsTheRoleThatHoldsIt(string method, string path, string permission)
    {
        var role = permission is ChoRolePermissions.WorkQueueRead or ChoRolePermissions.WorkQueueWork ? Examiner : Supervisor;

        var response = await _factory.Client("someone", role).SendAsync(Request(method, path));

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithTheToken_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("alice", "tenant-a", [Examiner]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        (await client.GetAsync("/api/v1/work/queues")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Flows ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetNext_LeasesTheTopItem_AndAnswersWithItsOpenUrl_Then204WhenEmpty()
    {
        await Join("alice", "claim.ncci");
        await PendedClaim("claim-1");
        var alice = _factory.Client("alice", Examiner);

        var next = await alice.PostAsync("/api/v1/work/next", null);

        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(next);
        body["openUrl"]!.GetValue<string>().Should().Be("/claims/claim-1");
        body["item"]!["queueName"]!.GetValue<string>().Should().Be("NCCI / MUE Edit Failures");
        body["item"]!["assignedTo"]!.GetValue<string>().Should().Be("alice");
        body["item"]!["priorityReason"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["resumed"]!.GetValue<bool>().Should().BeFalse();

        var mine = await Json(await alice.GetAsync("/api/v1/work/mine"));
        mine.AsArray().Should().ContainSingle();

        var bob = _factory.Client("bob", Examiner);
        await Join("bob", "claim.ncci");
        (await bob.PostAsync("/api/v1/work/next", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ReleaseAndRenew_AreTheHoldersOnly()
    {
        await Join("alice", "claim.ncci");
        await PendedClaim("claim-1");
        var alice = _factory.Client("alice", Examiner);
        var id = (await Json(await alice.PostAsync("/api/v1/work/next", null)))["item"]!["id"]!.GetValue<string>();
        var bob = _factory.Client("bob", Examiner);

        (await bob.PostAsync($"/api/v1/work/{id}/renew", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await alice.PostAsync($"/api/v1/work/{id}/renew", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await bob.PostAsync($"/api/v1/work/{id}/release", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await alice.PostAsync($"/api/v1/work/{id}/release", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await alice.PostAsync($"/api/v1/work/{id}/release", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await alice.PostAsync("/api/v1/work/missing/renew", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SupervisorReassign_Persists_AndTheActorIsTheToken()
    {
        await PendedClaim("claim-1");
        var id = WorkTestKit.ItemId("claim-1");
        var sue = _factory.Client("sue", Supervisor);

        var assigned = await sue.PostAsJsonAsync($"/api/v1/work/{id}/assign", new { userId = "alice", assignedBy = "attacker" });

        assigned.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = (await _factory.Store.GetItemAsync(WorkTestKit.Tenant, id))!;
        stored.AssignedTo.Should().Be("alice");
        stored.AssignedBy.Should().Be("sue");
        stored.Status.Should().Be(WorkItemStatus.Leased);

        var list = await Json(await sue.GetAsync("/api/v1/work?assignedTo=alice"));
        list["total"]!.GetValue<int>().Should().Be(1);

        (await sue.PostAsJsonAsync($"/api/v1/work/{id}/assign", new { userId = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sue.PostAsync($"/api/v1/work/{id}/unassign", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.Store.GetItemAsync(WorkTestKit.Tenant, id))!.AssignedTo.Should().BeNull();

        var detail = await Json(await sue.GetAsync($"/api/v1/work/{id}"));
        detail["audit"]!.AsArray().Select(a => a!["action"]!.GetValue<string>())
            .Should().Contain([WorkAuditActions.Assigned, WorkAuditActions.Unassigned]);
    }

    [Fact]
    public async Task Queues_ListsCounts()
    {
        await PendedClaim("claim-1", "COB");
        await PendedClaim("claim-2", "COB");

        var queues = await Json(await _factory.Client("alice", Examiner).GetAsync("/api/v1/work/queues"));

        var cob = queues.AsArray().Single(q => q!["id"]!.GetValue<string>() == WorkTestKit.QueueId("claim.cob"))!;
        cob["open"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Admin_QueueCrud_Validation_And_Recompute()
    {
        var admin = _factory.Client("admin", Admin);
        await PendedClaim("big", amount: 50_000m);

        var bad = await admin.PostAsJsonAsync("/api/v1/work/admin/queues", new { name = "", workType = "Claim" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var created = await admin.PostAsJsonAsync("/api/v1/work/admin/queues", new
        {
            name = "High dollar", workType = "Claim", order = 1, slaDays = 15,
            criteria = new { minAmount = 10_000 },
            weights = new { deadline = 1, age = 0.5, amount = 1, expedited = 1 },
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var queueId = (await Json(created))["id"]!.GetValue<string>();

        var recompute = await Json(await admin.PostAsync("/api/v1/work/admin/recompute", null));
        recompute["rerouted"]!.GetValue<int>().Should().Be(1);
        (await _factory.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("big")))!.QueueId.Should().Be(queueId);

        (await admin.DeleteAsync($"/api/v1/work/admin/queues/{queueId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/work/admin/queues/{queueId}", new
        {
            name = "High dollar", workType = "Claim", isActive = false, slaDays = 15,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        await admin.PostAsync("/api/v1/work/admin/recompute", null);
        (await admin.DeleteAsync($"/api/v1/work/admin/queues/{queueId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync("/api/v1/work/admin/queues/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_TeamsMemberships_EffectiveQueues()
    {
        var admin = _factory.Client("sue", Supervisor);
        var team = await Json(await admin.PostAsJsonAsync("/api/v1/work/admin/teams", new { name = "COB pod", userIds = new[] { "alice" } }));
        await admin.GetAsync("/api/v1/work/admin/queues"); // first use seeds the defaults
        var membership = await admin.PostAsJsonAsync("/api/v1/work/admin/memberships", new
        {
            teamId = team["id"]!.GetValue<string>(), queueId = WorkTestKit.QueueId("claim.cob"), rank = 1, authorityLimit = 2500,
        });
        membership.StatusCode.Should().Be(HttpStatusCode.OK);

        var effective = await Json(await admin.GetAsync("/api/v1/work/admin/users/alice/queues"));
        var only = effective.AsArray().Single()!;
        only["queueName"]!.GetValue<string>().Should().Be("Coordination of Benefits");
        only["authorityLimit"]!.GetValue<decimal>().Should().Be(2500m);

        (await Json(await admin.GetAsync("/api/v1/work/assignees"))).AsArray().Select(a => a!.GetValue<string>())
            .Should().Equal("alice");
    }

    [Fact]
    public async Task Reconcile_WhenClaimsServiceIsDown_Is502_AndChangesNothing()
    {
        _factory.Claims.Fail = true;

        var response = await _factory.Client("admin", Admin).PostAsync("/api/v1/work/admin/reconcile", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ── Raising and closing a follow-up over HTTP ────────────────────

    private static object FollowUp() => new
    {
        workType = "Inquiry",
        title = "Call the member back about the denial",
        sourceNumber = "C-20261023-AAAA1111",
        openUrl = "/calls/contact-1",
        reasonCodes = new[] { "CLAIM_DENIAL" },
        facets = new Dictionary<string, string[]> { ["channel"] = ["Phone"] },
        memberId = "M1",
    };

    [Fact]
    public async Task AServiceRep_RaisesAFollowUp_AndClosesIt()
    {
        var rep = _factory.Client("rep-1", ChoRolePermissions.MemberServices);

        var created = await rep.PostAsJsonAsync("/api/v1/work", FollowUp());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await Json(created);
        item["queueName"]!.GetValue<string>().Should().Be("Follow-up: Claims and Payment");
        item["openUrl"]!.GetValue<string>().Should().Be("/calls/contact-1");

        var id = item["id"]!.GetValue<string>();
        var closed = await rep.PostAsJsonAsync($"/api/v1/work/{id}/complete", new { resolution = "Explained the denial" });
        closed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(closed))["status"]!.GetValue<string>().Should().Be("Done");
    }

    [Fact]
    public async Task AnExaminer_CannotRaiseAFollowUp()
    {
        // workqueue:work gets them to the endpoint; the work type's own
        // entitlement (service:contact) is what decides.
        var response = await _factory.Client("examiner", Examiner).PostAsJsonAsync("/api/v1/work", FollowUp());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ClaimWork_CannotBeCreatedThroughTheApi()
    {
        var response = await _factory.Client("admin", Admin).PostAsJsonAsync("/api/v1/work", new
        {
            workType = "Claim", title = "a claim that is not pended",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response))["error"]!.GetValue<string>().Should().Contain("source service");
    }

    [Fact]
    public async Task AnOpenUrl_ThatLeavesThePortal_IsRefused()
    {
        var response = await _factory.Client("rep-1", ChoRolePermissions.MemberServices)
            .PostAsJsonAsync("/api/v1/work", new
            {
                workType = "Inquiry", title = "a follow-up", openUrl = "https://evil.test/steal",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response))["error"]!.GetValue<string>().Should().Contain("relative portal path");
    }

    [Fact]
    public async Task AClaimItem_CannotBeCompletedFromTheWorkList()
    {
        await PendedClaim("claim-1");

        var response = await _factory.Client("sue", Supervisor)
            .PostAsJsonAsync($"/api/v1/work/{WorkTestKit.ItemId("claim-1")}/complete", new { resolution = "done" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnotherTenantsItems_AreNotVisible()
    {
        await PendedClaim("claim-1");
        var otherTenant = _factory.TenantClient("tenant-b", "sue", Supervisor);

        (await otherTenant.GetAsync($"/api/v1/work/{WorkTestKit.ItemId("claim-1")}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await otherTenant.GetAsync("/api/v1/work")))["total"]!.GetValue<int>().Should().Be(0);
    }
}
