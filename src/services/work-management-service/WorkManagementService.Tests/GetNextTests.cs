using CloudHealthOffice.Infrastructure.Security;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

/// <summary>Distribution: Get Next ordering, eligibility, leases and push.</summary>
public class GetNextTests
{
    private readonly WorkTestKit _kit = new();
    private readonly TestActor _alice = new("alice");
    private readonly TestActor _bob = new("bob");
    private readonly TestActor _supervisor = new("sue", WorkTestKit.Tenant, ChoRolePermissions.ClaimsSupervisor);

    private async Task Pend(string claimId, string code = "NCCI", decimal amount = 100m, int receivedDaysAgo = 1)
    {
        var outcome = await _kit.Intake().ApplyClaimEventAsync(_kit.Needed(claimId, code, amount, received: _kit.Now.AddDays(-receivedDaysAgo)));
        outcome.Should().Be(IntakeOutcome.Created);
    }

    private Task<DistributionResult?> Next(TestActor actor, WorkType? type = null) => _kit.Distribution().GetNextAsync(actor, type);

    [Fact]
    public async Task NoMemberships_NothingToPull()
    {
        await Pend("c1");

        (await Next(_alice)).Should().BeNull();
    }

    [Fact]
    public async Task HighestScore_First_WithinTheCallersQueues()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await Pend("young", receivedDaysAgo: 1);
        await Pend("old", receivedDaysAgo: 20);
        await Pend("cob-older", "COB", receivedDaysAgo: 25); // not alice's queue

        var first = await Next(_alice);

        first!.Item!.SourceId.Should().Be("old");
        first.Item.Status.Should().Be(WorkItemStatus.Leased);
        first.Item.AssignedTo.Should().Be("alice");
        first.Item.LeaseExpiresAt.Should().Be(_kit.Now.AddMinutes(30));
    }

    [Fact]
    public async Task PrimaryQueues_BeforeOverflow_EvenWhenOverflowScoresHigher()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.JoinAsync("alice", "claim.cob", overflow: true);
        await Pend("cob-urgent", "COB", receivedDaysAgo: 40);
        await Pend("ncci-fresh", "NCCI", receivedDaysAgo: 0);

        (await Next(_alice))!.Item!.SourceId.Should().Be("ncci-fresh");
        await _kit.Distribution().ReleaseAsync(_alice, WorkTestKit.ItemId("ncci-fresh"));
        await _kit.Intake().ApplyClaimEventAsync(_kit.Resolved("ncci-fresh"));

        (await Next(_alice))!.Item!.SourceId.Should().Be("cob-urgent", "overflow is pulled once the primary queues are empty");
    }

    [Fact]
    public async Task AuthorityLimit_KeepsLargerItemsAway()
    {
        await _kit.JoinAsync("alice", "claim.ncci", limit: 1_000m);
        await Pend("big", amount: 50_000m, receivedDaysAgo: 20);
        await Pend("small", amount: 900m, receivedDaysAgo: 1);

        (await Next(_alice))!.Item!.SourceId.Should().Be("small");
        (await Next(new TestActor("carol")))!.Should().BeNull("carol has no membership");
    }

    [Fact]
    public async Task OwnActiveLease_IsReturnedFirst_NotANewItem()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await Pend("a1", receivedDaysAgo: 10);
        await Pend("a2", receivedDaysAgo: 20);

        var first = await Next(_alice);
        var again = await Next(_alice);

        again!.Item!.Id.Should().Be(first!.Item!.Id);
        again.Resumed.Should().BeTrue();
    }

    [Fact]
    public async Task OthersActiveLeases_AreSkipped_ExpiredLeases_AreReclaimed()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.JoinAsync("bob", "claim.ncci");
        await Pend("only");

        (await Next(_alice))!.Item!.SourceId.Should().Be("only");
        (await Next(_bob)).Should().BeNull("alice holds the only item");

        _kit.Clock.Advance(TimeSpan.FromMinutes(31));
        var reclaimed = await Next(_bob);

        reclaimed!.Item!.SourceId.Should().Be("only");
        reclaimed.Item.AssignedTo.Should().Be("bob");
        var audit = await _kit.Store.ListAuditAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("only"));
        audit.Should().Contain(a => a.Action == WorkAuditActions.Reclaimed && a.FromUser == "alice" && a.ToUser == "bob");
    }

    [Fact]
    public async Task Entitlement_ARoleWithoutClaimsWork_GetsNothing_WhateverItsMemberships()
    {
        var memberServices = new TestActor("mia", WorkTestKit.Tenant, ChoRolePermissions.MemberServices);
        await _kit.JoinAsync("mia", "claim.ncci");
        await Pend("c1");

        (await Next(memberServices)).Should().BeNull();
    }

    [Fact]
    public async Task WorkTypeFilter_OnlyThatType()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await Pend("c1");

        (await Next(_alice, WorkType.Appeal)).Should().BeNull();
        (await Next(_alice, WorkType.Claim))!.Item!.SourceId.Should().Be("c1");
    }

    [Fact]
    public async Task TeamMemberships_ApplyToEveryMember()
    {
        await _kit.Config().EnsureSeededAsync(WorkTestKit.Tenant);
        var team = (await _kit.Config().SaveTeamAsync(WorkTestKit.Tenant, null, new TeamInput { Name = "COB team", UserIds = ["alice", "bob"] }, "sue")).Value!;
        (await _kit.Config().SaveMembershipAsync(WorkTestKit.Tenant, null,
            new MembershipInput { TeamId = team.Id, QueueId = WorkTestKit.QueueId("claim.cob") }, "sue")).Error.Should().BeNull();
        await Pend("cob-1", "COB");
        await Pend("cob-2", "COB");

        var a = await Next(_alice);
        var b = await Next(_bob);

        new[] { a!.Item!.SourceId, b!.Item!.SourceId }.Should().BeEquivalentTo(["cob-1", "cob-2"]);
    }

    [Fact]
    public async Task Release_ReturnsTheItemToItsQueue_OnlyTheHolderMayRelease()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.JoinAsync("bob", "claim.ncci");
        await Pend("c1");
        var id = (await Next(_alice))!.Item!.Id;

        (await _kit.Distribution().ReleaseAsync(_bob, id)).Status.Should().Be(DistributionStatus.Forbidden);
        (await _kit.Distribution().ReleaseAsync(_alice, id)).Status.Should().Be(DistributionStatus.Ok);

        (await Next(_bob))!.Item!.Id.Should().Be(id);
    }

    [Fact]
    public async Task Renew_ExtendsTheLease_FromNow()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await Pend("c1");
        var id = (await Next(_alice))!.Item!.Id;
        _kit.Clock.Advance(TimeSpan.FromMinutes(20));

        var renewed = await _kit.Distribution().RenewAsync(_alice, id);

        renewed.Status.Should().Be(DistributionStatus.Ok);
        renewed.Item!.LeaseExpiresAt.Should().Be(_kit.Now.AddMinutes(30));
        (await _kit.Distribution().RenewAsync(_bob, id)).Status.Should().Be(DistributionStatus.Forbidden);
    }

    [Fact]
    public async Task LeaseDuration_IsConfigurable()
    {
        _kit.Options.LeaseMinutes = 5;
        await _kit.JoinAsync("alice", "claim.ncci");
        await Pend("c1");

        (await Next(_alice))!.Item!.LeaseExpiresAt.Should().Be(_kit.Now.AddMinutes(5));
    }

    [Fact]
    public async Task Assign_PushesToAUser_Reassign_And_Unassign_AreAudited()
    {
        await Pend("c1");
        var id = WorkTestKit.ItemId("c1");

        var assigned = await _kit.Distribution().AssignAsync(_supervisor, id, "alice");
        assigned.Item!.AssignedTo.Should().Be("alice");
        assigned.Item.AssignedBy.Should().Be("sue");
        assigned.Item.LeaseExpiresAt.Should().Be(_kit.Now.AddMinutes(480));

        // The assignee's Get Next returns the pushed item first (own lease), membership or not.
        (await Next(_alice))!.Item!.Id.Should().Be(id);

        (await _kit.Distribution().AssignAsync(_supervisor, id, "bob")).Item!.AssignedTo.Should().Be("bob");
        (await _kit.Distribution().UnassignAsync(_supervisor, id)).Item!.Status.Should().Be(WorkItemStatus.Open);

        var audit = await _kit.Store.ListAuditAsync(WorkTestKit.Tenant, id);
        audit.Select(a => a.Action).Should().ContainInOrder(
            WorkAuditActions.Created, WorkAuditActions.Assigned, WorkAuditActions.Reassigned, WorkAuditActions.Unassigned);
        audit.Single(a => a.Action == WorkAuditActions.Reassigned).FromUser.Should().Be("alice");
    }

    [Fact]
    public async Task Assign_ACompletedItem_IsAConflict()
    {
        await Pend("c1");
        await _kit.Intake().ApplyClaimEventAsync(_kit.Resolved("c1"));

        (await _kit.Distribution().AssignAsync(_supervisor, WorkTestKit.ItemId("c1"), "alice")).Status
            .Should().Be(DistributionStatus.Conflict);
        (await _kit.Distribution().AssignAsync(_supervisor, "missing", "alice")).Status
            .Should().Be(DistributionStatus.NotFound);
    }

    [Fact]
    public async Task Tenants_AreIsolated()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("other-tenant-claim", tenant: "tenant-b"));

        (await Next(_alice)).Should().BeNull();
    }
}
