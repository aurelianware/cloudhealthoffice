using System.Collections.Concurrent;
using CloudHealthOffice.Testing.Mongo;
using WorkManagementService.Models;
using WorkManagementService.Repositories;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

/// <summary>
/// The Get Next guarantee, against each store: however many callers pull at
/// once, no item is ever handed to two of them, and nothing eligible is left
/// behind while a caller came back empty-handed.
/// </summary>
public static class GetNextConcurrency
{
    public const int Callers = 24;

    public static async Task NoItemIsHandedOutTwice(IWorkStore store, int items)
    {
        var kit = new WorkTestKit(store);
        var tenant = WorkTestKit.Tenant;
        for (var i = 0; i < Callers; i++)
            await kit.JoinAsync($"user-{i}", "claim.ncci");
        for (var i = 0; i < items; i++)
            await kit.Intake().ApplyClaimEventAsync(kit.Needed($"claim-{i}", received: kit.Now.AddDays(-i % 7)));

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, Callers).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await kit.Distribution().GetNextAsync(new TestActor($"user-{i}", tenant), null);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(tasks);

        var won = results.Where(r => r?.Item is not null).Select(r => r!.Item!.Id).ToList();
        won.Should().OnlyHaveUniqueItems("an item leased by one caller must never be returned to another");
        won.Should().HaveCount(Math.Min(items, Callers), "every caller gets an item while any is left");

        // The store agrees: each won item is leased to exactly the caller that got it.
        var holders = new ConcurrentDictionary<string, string>();
        foreach (var (result, i) in results.Select((r, i) => (r, i)).Where(x => x.r?.Item is not null))
        {
            var stored = await store.GetItemAsync(tenant, result!.Item!.Id);
            stored!.Status.Should().Be(WorkItemStatus.Leased);
            stored.AssignedTo.Should().Be($"user-{i}");
            holders[stored.Id] = stored.AssignedTo!;
        }
        holders.Should().HaveCount(won.Count);
    }

    /// <summary>The compare-and-swap itself: of many writers holding the same version, exactly one wins.</summary>
    public static async Task ConditionalLease_HasExactlyOneWinner(IWorkStore store)
    {
        var kit = new WorkTestKit(store);
        await kit.Intake().ApplyClaimEventAsync(kit.Needed("contended"));
        var id = WorkTestKit.ItemId("contended");
        var copies = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => store.GetItemAsync(WorkTestKit.Tenant, id)));
        var original = copies[0]!.Version;

        var wins = await Task.WhenAll(copies.Select((copy, i) => Task.Run(async () =>
        {
            var expected = copy!.Version;
            copy.Status = WorkItemStatus.Leased;
            copy.SetLease($"user-{i}", $"user-{i}", kit.Now.AddMinutes(30));
            return await store.TryReplaceItemAsync(copy, expected);
        })));

        wins.Count(w => w).Should().Be(1);
        (await store.GetItemAsync(WorkTestKit.Tenant, id))!.Version.Should().Be(original + 1);
    }
}

public class InMemoryConcurrencyTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(24)]
    [InlineData(60)]
    public Task ParallelGetNext_NeverReturnsTheSameItemTwice(int items) =>
        GetNextConcurrency.NoItemIsHandedOutTwice(new InMemoryWorkStore(), items);

    [Fact]
    public Task ConditionalLease_HasExactlyOneWinner() =>
        GetNextConcurrency.ConditionalLease_HasExactlyOneWinner(new InMemoryWorkStore());

    [Fact]
    public Task Store_RoundTrips_QueriesAndCounts() => StoreContract.Exercise(new InMemoryWorkStore());
}

/// <summary>The same guarantees against a real mongod (ReplaceOne filtered on version).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class MongoConcurrencyTests
{
    private readonly MongoRunnerFixture _mongo;

    public MongoConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    public async Task ParallelGetNext_NeverReturnsTheSameItemTwice(int items)
    {
        var db = _mongo.CreateDatabase("workmgmt_getnext");
        try
        {
            await GetNextConcurrency.NoItemIsHandedOutTwice(new MongoWorkStore(db), items);
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }

    [Fact]
    public async Task ConditionalLease_HasExactlyOneWinner()
    {
        var db = _mongo.CreateDatabase("workmgmt_cas");
        try
        {
            await GetNextConcurrency.ConditionalLease_HasExactlyOneWinner(new MongoWorkStore(db));
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }

    [Fact]
    public async Task Store_RoundTrips_QueriesAndCounts()
    {
        var db = _mongo.CreateDatabase("workmgmt_store");
        try
        {
            await StoreContract.Exercise(new MongoWorkStore(db));
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }
}

/// <summary>Store behaviour every implementation must share (queries, filters, counts, isolation).</summary>
public static class StoreContract
{
    public static async Task Exercise(IWorkStore store)
    {
        var kit = new WorkTestKit(store);
        const string t = WorkTestKit.Tenant;
        await kit.JoinAsync("alice", "claim.ncci", limit: 1_000m);
        await kit.Intake().ApplyClaimEventAsync(kit.Needed("small", amount: 500m, received: kit.Now.AddDays(-10)));
        await kit.Intake().ApplyClaimEventAsync(kit.Needed("large", amount: 5_000m, received: kit.Now.AddDays(-20)));
        await kit.Intake().ApplyClaimEventAsync(kit.Needed("cob", "COB"));
        await kit.Intake().ApplyClaimEventAsync(kit.Needed("elsewhere", tenant: "tenant-b"));

        var ncci = WorkTestKit.QueueId("claim.ncci");
        var nowMs = DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeMilliseconds();
        (await store.FindCandidatesAsync(t, ncci, null, nowMs, 10)).Select(i => i.SourceId).Should().Equal("large", "small");
        (await store.FindCandidatesAsync(t, ncci, 1_000m, nowMs, 10)).Select(i => i.SourceId).Should().Equal("small");

        var leased = await kit.Distribution().GetNextAsync(new TestActor("alice"), null);
        leased!.Item!.SourceId.Should().Be("small");
        (await store.ListLeasedToAsync(t, "alice")).Should().ContainSingle(i => i.SourceId == "small");

        var counts = (await store.CountByQueueAsync(t)).ToDictionary(c => c.QueueId);
        counts[ncci].Open.Should().Be(1);
        counts[ncci].Leased.Should().Be(1);
        counts[WorkTestKit.QueueId("claim.cob")].Open.Should().Be(1);

        var (page, total) = await store.QueryItemsAsync(t, new WorkItemQuery(PageSize: 2));
        total.Should().Be(3);
        page.Should().HaveCount(2);
        page.Select(i => i.PriorityScore).Should().BeInDescendingOrder();
        (await store.QueryItemsAsync(t, new WorkItemQuery(AssignedTo: "alice"))).Total.Should().Be(1);
        (await store.QueryItemsAsync(t, new WorkItemQuery(Status: WorkItemStatus.Open, QueueId: ncci))).Total.Should().Be(1);

        (await store.ListActiveItemsAsync(t, WorkType.Claim)).Should().HaveCount(3);
        (await store.ListTenantsAsync()).Should().Contain(["tenant-a", "tenant-b"]);
        (await store.ListAuditAsync(t, leased.Item.Id)).Select(a => a.Action)
            .Should().ContainInOrder(WorkAuditActions.Created, WorkAuditActions.Leased);
        (await store.GetItemAsync("tenant-b", WorkTestKit.ItemId("small"))).Should().BeNull("tenant isolation");

        // Events stamped with the same instant still read back in the order they were written.
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var actions = Enumerable.Range(0, 8).Select(i => $"step-{i}").ToList();
        foreach (var action in actions)
            await store.AppendAuditAsync(new WorkAuditEvent { TenantId = t, WorkItemId = "tie", Action = action, Actor = "test", At = at });
        (await store.ListAuditAsync(t, "tie")).Select(a => a.Action).Should().Equal(actions);
    }
}
