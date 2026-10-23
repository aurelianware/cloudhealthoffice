using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

/// <summary>
/// Nobody is handed work they cannot do: supervisor push and queue membership
/// both check the other person's entitlement, and both refuse when the check
/// cannot be run at all.
/// </summary>
public class EntitlementGateTests
{
    private readonly WorkTestKit _kit = new();
    private readonly TestActor _supervisor = new("sue", WorkTestKit.Tenant, ChoRolePermissions.ClaimsSupervisor);

    private async Task<string> Pend(string claimId = "c1")
    {
        (await _kit.Intake().ApplyClaimEventAsync(_kit.Needed(claimId))).Should().Be(IntakeOutcome.Created);
        return WorkTestKit.ItemId(claimId);
    }

    [Fact]
    public async Task Push_Checks_The_Assignees_Entitlement_Not_The_Supervisors()
    {
        var id = await Pend();
        _kit.Entitlements.Deny.Add(("rep-1", "claims:work"));

        var refused = await _kit.Distribution().AssignAsync(_supervisor, id, "rep-1");

        refused.Status.Should().Be(DistributionStatus.Conflict);
        refused.Error.Should().Contain("claims:work").And.Contain("rep-1");
        // Refused, not half-done: the item is still the queue's.
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, id))!.Status.Should().Be(WorkItemStatus.Open);
        _kit.Entitlements.Calls.Should().ContainSingle()
            .Which.Should().Be((WorkTestKit.Tenant, "rep-1", "claims:work"));
    }

    [Fact]
    public async Task Push_Is_Refused_When_The_Check_Cannot_Be_Run()
    {
        var id = await Pend();
        _kit.Entitlements.Unanswerable.Add("alice");

        var refused = await _kit.Distribution().AssignAsync(_supervisor, id, "alice");

        refused.Status.Should().Be(DistributionStatus.Conflict);
        refused.Error.Should().Contain("tenant-service did not answer");
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, id))!.Status.Should().Be(WorkItemStatus.Open);
    }

    [Fact]
    public async Task Push_To_An_Entitled_User_Still_Works()
    {
        var id = await Pend();

        var assigned = await _kit.Distribution().AssignAsync(_supervisor, id, "alice");

        assigned.Status.Should().Be(DistributionStatus.Ok);
        assigned.Item!.AssignedTo.Should().Be("alice");
    }

    [Fact]
    public async Task A_Missing_Item_Is_NotFound_Before_Any_Check_Is_Made()
    {
        (await _kit.Distribution().AssignAsync(_supervisor, "missing", "alice")).Status
            .Should().Be(DistributionStatus.NotFound);
        _kit.Entitlements.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Permission_Checked_Is_The_Items_Work_Type()
    {
        var item = await _kit.Intake().CreateAdHocAsync(new WorkIntakeService.AdHocWork(
            WorkTestKit.Tenant, WorkType.Inquiry, "C-20261023-AAAA1111", "Call back about a denial",
            "/calls/contact-1", ["CLAIM_DENIAL"], new Dictionary<string, List<string>>(),
            "M1", null, "Medicare", false, null, "rep-1"));

        await _kit.Distribution().AssignAsync(_supervisor, item.Id, "rep-2");

        _kit.Entitlements.Calls.Should().Contain(
            (WorkTestKit.Tenant, "rep-2", WorkEntitlements.PermissionFor(WorkType.Inquiry)));
    }

    [Fact]
    public async Task A_Queue_Will_Not_Take_A_Member_Who_Cannot_Work_Its_Type()
    {
        await _kit.Config().EnsureSeededAsync(WorkTestKit.Tenant);
        _kit.Entitlements.Deny.Add(("rep-1", "claims:work"));

        var refused = await _kit.Config().SaveMembershipAsync(WorkTestKit.Tenant, null, new MembershipInput
        {
            UserId = "rep-1", QueueId = WorkTestKit.QueueId("claim.ncci"), Rank = 1,
        }, "admin");

        refused.Status.Should().Be(400);
        refused.Error.Should().Contain("rep-1").And.Contain("claims:work");
    }

    [Fact]
    public async Task A_Membership_Is_Not_Saved_Unchecked()
    {
        await _kit.Config().EnsureSeededAsync(WorkTestKit.Tenant);
        _kit.Entitlements.Unanswerable.Add("rep-1");

        var refused = await _kit.Config().SaveMembershipAsync(WorkTestKit.Tenant, null, new MembershipInput
        {
            UserId = "rep-1", QueueId = WorkTestKit.QueueId("claim.ncci"), Rank = 1,
        }, "admin");

        refused.Status.Should().Be(409);
        refused.Error.Should().Contain("did not answer");
        (await _kit.Store.ListMembershipsAsync(WorkTestKit.Tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Teams_Membership_Checks_Every_Member()
    {
        await _kit.Config().EnsureSeededAsync(WorkTestKit.Tenant);
        var team = (await _kit.Config().SaveTeamAsync(WorkTestKit.Tenant, null, new TeamInput
        {
            Name = "Pod A", UserIds = ["alice", "bob", "rep-1"],
        }, "admin")).Value!;
        _kit.Entitlements.Deny.Add(("rep-1", "claims:work"));

        var refused = await _kit.Config().SaveMembershipAsync(WorkTestKit.Tenant, null, new MembershipInput
        {
            TeamId = team.Id, QueueId = WorkTestKit.QueueId("claim.ncci"), Rank = 1,
        }, "admin");

        refused.Status.Should().Be(400);
        refused.Error.Should().Contain("rep-1");
        refused.Error.Should().NotContain("alice"); // only the ones that failed are named
    }
}

/// <summary>The directory itself: what it caches, and what it does with no answer.</summary>
public class WorkEntitlementDirectoryTests
{
    private const string Tenant = "tenant-a";

    /// <summary>Answers a scripted sequence, and counts the calls.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _replies = new();
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }

        public StubHandler Then(HttpStatusCode status, string body = "")
        {
            _replies.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        public StubHandler ThenThrows()
        {
            _replies.Enqueue(() => throw new HttpRequestException("tenant-service down"));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastPath = request.RequestUri?.PathAndQuery;
            if (_replies.Count == 0) throw new InvalidOperationException("No scripted reply left.");
            return Task.FromResult(_replies.Dequeue()());
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://tenant-service/") };
    }

    private static (WorkEntitlementDirectory Directory, ManualTimeProvider Clock) Build(StubHandler handler)
    {
        var clock = new ManualTimeProvider();
        return (new WorkEntitlementDirectory(new StubFactory(handler), clock,
            NullLogger<WorkEntitlementDirectory>.Instance), clock);
    }

    private static string Granted(bool granted) => $$"""{"userId":"u","permission":"claims:work","granted":{{(granted ? "true" : "false")}}}""";

    [Fact]
    public async Task It_Asks_Tenant_Service_And_Reads_The_Granted_Flag()
    {
        var handler = new StubHandler().Then(HttpStatusCode.OK, Granted(true));
        var (directory, _) = Build(handler);

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Granted);
        handler.LastPath.Should().Be("/api/v1/tenants/tenant-a/users/alice/permissions/claims%3Awork");
    }

    [Fact]
    public async Task A_Fresh_Answer_Is_Reused_Then_Asked_Again()
    {
        var handler = new StubHandler().Then(HttpStatusCode.OK, Granted(true)).Then(HttpStatusCode.OK, Granted(false));
        var (directory, clock) = Build(handler);

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Granted);
        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Granted);
        handler.Calls.Should().Be(1);

        clock.Advance(WorkEntitlementDirectory.FreshFor + TimeSpan.FromSeconds(1));
        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Denied);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_Unknown_User_Is_A_Denial_Not_A_Failure()
    {
        var handler = new StubHandler().Then(HttpStatusCode.NotFound, """{"error":"User typo not found"}""");
        var (directory, _) = Build(handler);

        (await directory.CheckAsync(Tenant, "typo", "claims:work")).Should().Be(Entitlement.Denied);
    }

    [Fact]
    public async Task A_Blip_Serves_The_Last_Known_Answer_Rather_Than_None()
    {
        var handler = new StubHandler().Then(HttpStatusCode.OK, Granted(true)).ThenThrows();
        var (directory, clock) = Build(handler);

        await directory.CheckAsync(Tenant, "alice", "claims:work");
        clock.Advance(WorkEntitlementDirectory.FreshFor + TimeSpan.FromMinutes(1));

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Granted);
    }

    [Fact]
    public async Task Past_The_Grace_Window_There_Is_No_Answer()
    {
        var handler = new StubHandler().Then(HttpStatusCode.OK, Granted(true)).ThenThrows();
        var (directory, clock) = Build(handler);

        await directory.CheckAsync(Tenant, "alice", "claims:work");
        clock.Advance(WorkEntitlementDirectory.StaleGraceFor + TimeSpan.FromMinutes(1));

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Unknown);
    }

    [Fact]
    public async Task With_Nothing_Cached_An_Outage_Is_Unknown_And_Is_Not_Remembered()
    {
        var handler = new StubHandler().ThenThrows().Then(HttpStatusCode.OK, Granted(true));
        var (directory, _) = Build(handler);

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Unknown);
        // A blip must not pin the user out: the very next check asks again.
        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Granted);
    }

    [Theory]
    [InlineData("""{"granted":true}""", true)]
    [InlineData("""{"granted":false}""", false)]
    [InlineData("""{"userId":"u"}""", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public void The_Granted_Flag_Is_Read_Strictly(string json, bool? expected) =>
        WorkEntitlementDirectory.ParseGranted(json).Should().Be(expected);

    [Fact]
    public async Task A_Server_Error_With_Nothing_Cached_Is_Unknown()
    {
        var handler = new StubHandler().Then(HttpStatusCode.InternalServerError, "boom");
        var (directory, _) = Build(handler);

        (await directory.CheckAsync(Tenant, "alice", "claims:work")).Should().Be(Entitlement.Unknown);
    }
}
