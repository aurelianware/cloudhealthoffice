using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

public class ConfigTests
{
    private readonly WorkTestKit _kit = new();
    private const string T = WorkTestKit.Tenant;

    [Fact]
    public async Task DefaultQueues_AreSeededOnFirstUse_Once_EvenFromTwoProcesses()
    {
        var first = await _kit.Config().ListQueuesAsync(T);
        // A second replica (its own seed tracker) seeding the same tenant concurrently.
        var otherProcess = new WorkConfigService(_kit.Store, new WorkSeedTracker(), _kit.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkConfigService>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => otherProcess.EnsureSeededAsync(T)));

        var queues = await _kit.Store.ListQueuesAsync(T);
        queues.Should().HaveCount(
            DefaultClaimQueues.All.Count + DefaultEnrollmentQueues.All.Count + DefaultInquiryQueues.All.Count);
        first.Where(q => q.WorkType == WorkType.Claim).Select(q => q.SystemKey)
            .Should().Equal(DefaultClaimQueues.All.OrderBy(d => d.Order).Select(d => d.Key));
        first.Where(q => q.WorkType == WorkType.Enrollment834).Select(q => q.SystemKey)
            .Should().Equal(DefaultEnrollmentQueues.All.OrderBy(d => d.Order).Select(d => d.Key));
        first.Where(q => q.WorkType == WorkType.Inquiry).Select(q => q.SystemKey)
            .Should().Equal(DefaultInquiryQueues.All.OrderBy(d => d.Order).Select(d => d.Key));
        queues.Count(q => q.IsFallback).Should().Be(3, "one fallback per work type");
    }

    [Fact]
    public async Task SeedMembers_GetEveryActiveClaimQueue_OnceAndNotOverAnAdminsSetup()
    {
        _kit.Options.SeedMemberUserIds = ["presenter", "admin-configured"];
        // An admin already gave this user one queue: the seed leaves them alone.
        await _kit.Store.UpsertMembershipAsync(new WorkMembership
        {
            Id = "admin-set", TenantId = T, UserId = "admin-configured", QueueId = "any-queue",
        });

        await _kit.Config().EnsureSeededAsync(T);
        // A restart (fresh seed tracker) changes nothing.
        _kit.Seeds.Clear();
        await _kit.Config().EnsureSeededAsync(T);

        var memberships = await _kit.Store.ListMembershipsAsync(T);
        var claimQueues = (await _kit.Store.ListQueuesAsync(T))
            .Where(q => q.WorkType == WorkType.Claim && q.IsActive).ToList();
        claimQueues.Should().HaveCount(DefaultClaimQueues.All.Count);
        memberships.Where(m => m.UserId == "presenter").Select(m => m.QueueId)
            .Should().BeEquivalentTo(claimQueues.Select(q => q.Id), "one primary membership per claim queue, no duplicates; enrollment queues wait for their screen");
        memberships.Where(m => m.UserId == "admin-configured").Select(m => m.Id).Should().Equal("admin-set");
        (await _kit.Config().EffectiveQueuesAsync(T, "presenter")).Should().HaveCount(claimQueues.Count);
    }

    [Fact]
    public async Task NoSeedMembers_ByDefault()
    {
        await _kit.Config().EnsureSeededAsync(T);

        (await _kit.Store.ListMembershipsAsync(T)).Should().BeEmpty();
    }

    [Fact]
    public async Task ATenantThatAlreadyHasClaimQueues_IsNotReseeded()
    {
        await _kit.Config().ListQueuesAsync(T);
        var ncci = WorkTestKit.QueueId("claim.ncci");
        (await _kit.Config().DeleteQueueAsync(T, ncci)).Error.Should().BeNull();

        _kit.Seeds.Clear();
        await _kit.Config().EnsureSeededAsync(T);

        (await _kit.Store.GetQueueAsync(T, ncci)).Should().BeNull("an admin's deletion is respected");
    }

    [Theory]
    [InlineData("", "Name is required")]
    [InlineData("Medical Review", "already exists")]
    public async Task Create_ValidatesNames(string name, string error)
    {
        var result = await _kit.Config().CreateQueueAsync(T, new QueueInput { Name = name }, "admin");

        result.Status.Should().Be(400);
        result.Error.Should().Contain(error);
    }

    [Fact]
    public async Task Create_ValidatesSlaWeightsAndAmounts()
    {
        var config = _kit.Config();
        (await config.CreateQueueAsync(T, new QueueInput { Name = "x", SlaDays = 0 }, "a")).Status.Should().Be(400);
        (await config.CreateQueueAsync(T, new QueueInput { Name = "x", Weights = new PriorityWeights { Age = -1 } }, "a")).Status.Should().Be(400);
        (await config.CreateQueueAsync(T, new QueueInput
        {
            Name = "x", Criteria = new WorkQueueCriteria { MinAmount = 10, MaxAmount = 5 },
        }, "a")).Status.Should().Be(400);
        (await config.CreateQueueAsync(T, new QueueInput
        {
            Name = "x", Criteria = new WorkQueueCriteria { Facets = new() { ["Not A Facet"] = ["x"] } },
        }, "a")).Error.Should().Contain("Facet name");
    }

    [Fact]
    public async Task OneActiveFallbackPerWorkType()
    {
        var config = _kit.Config();
        (await config.CreateQueueAsync(T, new QueueInput { Name = "Second fallback", IsFallback = true }, "a"))
            .Error.Should().Contain("only one");

        var fallback = WorkTestKit.QueueId("claim.other");
        (await config.UpdateQueueAsync(T, fallback, new QueueInput { Name = "Other / Pending Review", IsFallback = true, IsActive = false }, "a"))
            .Error.Should().Contain("needs an active fallback");
        (await config.DeleteQueueAsync(T, fallback)).Status.Should().Be(409);
    }

    [Fact]
    public async Task AQueueWithOpenItems_CannotBeDeleted()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "COB"));

        var result = await _kit.Config().DeleteQueueAsync(T, WorkTestKit.QueueId("claim.cob"));

        result.Status.Should().Be(409);
        result.Error.Should().Contain("open item");
    }

    [Fact]
    public async Task Reorder_SetsRoutingOrder()
    {
        var cob = WorkTestKit.QueueId("claim.cob");
        var ncci = WorkTestKit.QueueId("claim.ncci");
        await _kit.Config().ListQueuesAsync(T);

        var result = await _kit.Config().ReorderAsync(T, WorkType.Claim, [cob, ncci], "admin");

        result.Error.Should().BeNull();
        var ordered = (await _kit.Config().ListQueuesAsync(T)).Where(q => q.WorkType == WorkType.Claim).Select(q => q.Id).ToList();
        ordered.IndexOf(cob).Should().Be(0);
        ordered.IndexOf(ncci).Should().Be(1);
        ordered.Last().Should().Be(WorkTestKit.QueueId("claim.other"), "the fallback is always last");
        (await _kit.Config().ReorderAsync(T, WorkType.Claim, ["nope"], "admin")).Status.Should().Be(400);
    }

    [Fact]
    public async Task Membership_Validation()
    {
        await _kit.Config().EnsureSeededAsync(T);
        var config = _kit.Config();
        var queue = WorkTestKit.QueueId("claim.ncci");

        (await config.SaveMembershipAsync(T, null, new MembershipInput { QueueId = queue }, "a")).Error.Should().Contain("exactly one");
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", TeamId = "t", QueueId = queue }, "a")).Status.Should().Be(400);
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", QueueId = "nope" }, "a")).Error.Should().Contain("not found");
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", QueueId = queue, AuthorityLimit = -1 }, "a")).Status.Should().Be(400);
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", QueueId = queue, Rank = 0 }, "a")).Status.Should().Be(400);
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", QueueId = queue }, "a")).Error.Should().BeNull();
        (await config.SaveMembershipAsync(T, null, new MembershipInput { UserId = "u", QueueId = queue }, "a")).Status.Should().Be(409);
    }

    [Fact]
    public async Task EffectiveQueues_MergeDirectAndTeam_StrongestTermsWin()
    {
        await _kit.Config().EnsureSeededAsync(T);
        var config = _kit.Config();
        var team = (await config.SaveTeamAsync(T, null, new TeamInput { Name = "Pod 1", UserIds = ["alice"] }, "sue")).Value!;
        await config.SaveMembershipAsync(T, null, new MembershipInput
        {
            UserId = "alice", QueueId = WorkTestKit.QueueId("claim.cob"), Rank = 3, IsOverflow = true, AuthorityLimit = 1_000m,
        }, "sue");
        await config.SaveMembershipAsync(T, null, new MembershipInput
        {
            TeamId = team.Id, QueueId = WorkTestKit.QueueId("claim.cob"), Rank = 2, IsOverflow = false, AuthorityLimit = 5_000m,
        }, "sue");
        await config.SaveMembershipAsync(T, null, new MembershipInput
        {
            UserId = "alice", QueueId = WorkTestKit.QueueId("claim.ncci"), Rank = 1, IsOverflow = true,
        }, "sue");

        var effective = await config.EffectiveQueuesAsync(T, "alice");

        effective.Should().HaveCount(2);
        var cob = effective[0];
        cob.Queue.SystemKey.Should().Be("claim.cob", "primary tier sorts first");
        cob.IsOverflow.Should().BeFalse();
        cob.Rank.Should().Be(2);
        cob.AuthorityLimit.Should().Be(5_000m);
        cob.Via.Should().BeEquivalentTo(["direct", "team:Pod 1"]);
        effective[1].IsOverflow.Should().BeTrue();
        effective[1].AuthorityLimit.Should().BeNull();

        (await config.AssigneesAsync(T)).Should().Equal("alice");
        (await config.DeleteTeamAsync(T, team.Id)).Error.Should().BeNull();
        (await config.ListMembershipsAsync(T)).Should().NotContain(m => m.TeamId == team.Id, "a team's memberships go with it");
    }
}
