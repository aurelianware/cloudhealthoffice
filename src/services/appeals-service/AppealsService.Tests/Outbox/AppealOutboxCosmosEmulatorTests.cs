using System.Text.Json;
using AppealsService.Middleware;
using AppealsService.Models;
using AppealsService.Repositories;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.Azure.Cosmos;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// <see cref="AppealOutboxStoreScenarios{TRepo}"/> against the Cosmos
/// <see cref="AppealRepository"/> on the Cosmos DB emulator: the ETag-pinned
/// read-modify-replace for every outbox / lease write (re-read on 412), and
/// the cross-partition sweep and backlog queries over the top-level sweep
/// fields (<see cref="AppealOutboxIndex"/>), evaluated by a real server
/// rather than the mocked container of <see cref="AppealOutboxCosmosTests"/>.
/// Documents go through the service's own serializer.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class AppealOutboxCosmosEmulatorTests(CosmosEmulatorFixture cosmos)
    : AppealOutboxStoreScenarios<AppealRepository>
{
    private Container _appeals = null!;

    public override async Task InitializeAsync()
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        // One database per test: the sweep and stats queries are cross-partition.
        var database = await cosmos.CreateDatabaseAsync(client, "appeals_outbox");
        _appeals = await CosmosEmulatorFixture.CreateContainerAsync(database, AppealRepository.AppealsContainerName);
        _repo = new AppealRepository(client, database.Id, _audit);

        // The Cosmos sweep field schedules a never-attempted entry at its
        // CreatedAt (wall clock, AppealOutbox.Require), where Mongo's due
        // filter treats it as due at once. The manual clock was frozen when
        // this class was built, a moment before the first write, so start it
        // a minute ahead; every scenario reasons relative to it.
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    public override Task DisposeAsync() => Task.CompletedTask;

    protected override async Task AssertStoredDocumentAsync(string appealId)
    {
        using var response = await _appeals.ReadItemStreamAsync(appealId, new PartitionKey(Tenant));
        using var document = await JsonDocument.ParseAsync(response.Content);
        var raw = document.RootElement;
        raw.GetProperty("outbox").GetArrayLength().Should().Be(3);
        raw.GetProperty("outbox")[0].GetProperty("status").GetString().Should().Be("pending");
        raw.GetProperty("outboxPendingCount").GetInt32().Should().Be(3, "the sweep fields describe the document they are in");
        raw.GetProperty("outboxNextDueAt").ValueKind.Should().Be(JsonValueKind.Number);
        raw.TryGetProperty("outboxLeaseUntilMs", out _).Should().BeFalse("no lease is held");
    }

    protected override async Task AppendLegacyTwinAsync(string appealId, AppealOutboxMessage first)
    {
        var read = await _appeals.ReadItemAsync<Appeal>(appealId, new PartitionKey(Tenant));
        var appeal = read.Resource;
        appeal.Outbox!.Add(new AppealOutboxMessage
        {
            IdempotencyKey = first.IdempotencyKey, EventId = first.EventId, EventType = first.EventType,
            TenantId = Tenant, AppealId = appealId, PayloadJson = first.PayloadJson, CreatedAt = DateTime.UtcNow,
            Status = AppealOutboxStatus.Pending, Attempts = 0,
        });
        AppealOutboxIndex.Refresh(appeal);
        await _appeals.ReplaceItemAsync(appeal, appealId, new PartitionKey(Tenant),
            new ItemRequestOptions { IfMatchEtag = read.ETag });
    }

    /// <summary>
    /// The outbox writes are ETag-pinned replaces: a dispatcher write that
    /// races an appeal change re-reads and lands on top of it (the 412 retry
    /// in <c>MutateOutboxAsync</c>), so neither write is lost.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_Appeal_Change_And_Lease_Writes_Both_Survive()
    {
        var appeal = await CreateAsync();
        var lease = Task.Run(() => _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)));
        var note = Task.Run(() => AddNoteAsync(appeal));
        await Task.WhenAll(lease, note);

        var stored = await StoredAsync(appeal.Id);
        stored.Notes.Should().ContainSingle();
        stored.Outbox!.Should().HaveCount(2);
        if (lease.Result is not null) stored.OutboxLeaseOwner.Should().Be("pod-a");
    }

    /// <summary>The sweep's cross-partition query sees every tenant, and skips leased appeals.</summary>
    [SkippableFact]
    public async Task Sweep_Finds_Due_Appeals_Across_Tenants_But_Not_Leased_Ones()
    {
        var a = await CreateAsync();
        var b = await CreateAsync("t-other");
        var leased = await CreateAsync();
        (await _repo.TryLeaseAsync(Tenant, leased.Id, "pod-a", Now, Now.AddSeconds(30))).Should().NotBeNull();

        var due = await _repo.FindDueAsync(Now.AddSeconds(1), 10);

        due.Should().BeEquivalentTo(new[] { new AppealOutboxKey(Tenant, a.Id), new AppealOutboxKey("t-other", b.Id) });
        (await _repo.FindDueAsync(Now.AddSeconds(1), 1)).Should().HaveCount(1, "the limit binds");
        (await _repo.FindDueAsync(Now.AddSeconds(31), 10)).Should().HaveCount(3, "an expired lease is due again");
    }
}
