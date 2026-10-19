using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// Optimistic concurrency on the MongoDB ID card store (EphemeralMongo). Revoke
/// and scan both read the card, changed it in memory and upserted the whole
/// document, so a scan that read the card before a revocation wrote it back
/// with RevokedAt = null: the revocation was silently undone. A save is now
/// conditional on the card's <see cref="IdCardRecord.Revision"/>; a scan or a
/// revocation that is overtaken re-reads and applies itself again.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class IdCardRecordConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-idcard";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public IdCardRecordConcurrencyTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("idcard_revision");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private MongoIdCardRecordRepository Records() => new(_db);

    private static IdCardOrchestrator Orchestrator(IIdCardRecordRepository records) =>
        new(null!, new InMemoryIdCardOrderRepository(), records, NullLogger<IdCardOrchestrator>.Instance);

    private async Task<IdCardRecord> IssueAsync(string cardId)
    {
        var record = new IdCardRecord { TenantId = Tenant, CardId = cardId, MemberId = "M1", OrderId = "O1" };
        await Records().UpsertAsync(record);
        return record;
    }

    private async Task<IdCardRecord> ReadAsync(string cardId) => (await Records().FindByCardIdAsync(Tenant, cardId))!;

    private static RevokeIdCardRequest Lost => new() { Reason = IdCardRevocationReason.Lost, Notes = "reported lost" };

    /// <summary>Runs <paramref name="afterRead"/> once, right after the first read: the read is then stale.</summary>
    private sealed class InterleavingRecords(IIdCardRecordRepository inner, Func<Task> afterRead) : IIdCardRecordRepository
    {
        private Func<Task>? _afterRead = afterRead;

        public int Reads { get; private set; }

        public async Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default)
        {
            var record = await inner.FindByCardIdAsync(tenantId, cardId, ct);
            Reads++;
            var hook = _afterRead;
            _afterRead = null;
            if (hook != null)
                await hook();
            return record;
        }

        public Task UpsertAsync(IdCardRecord record, CancellationToken ct = default) => inner.UpsertAsync(record, ct);
        public Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default) => inner.ListForMemberAsync(tenantId, memberId, ct);
        public Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default) => inner.ListIssuedSinceAsync(since, ct);
    }

    [Fact]
    public async Task A_scan_read_before_a_revoke_cannot_unrevoke_the_card_and_is_still_recorded()
    {
        await IssueAsync("card-1");
        var records = new InterleavingRecords(Records(),
            () => Orchestrator(Records()).RevokeAsync(Tenant, "card-1", "admin", Lost));

        var scanned = await Orchestrator(records).RecordScanAsync(Tenant, "card-1");

        var stored = await ReadAsync("card-1");
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(IdCardRevocationReason.Lost, stored.RevocationReason);
        Assert.Equal("admin", stored.RevokedBy);
        Assert.Equal(1, stored.ScanCount);
        Assert.NotNull(stored.LastScannedAt);
        Assert.NotNull(scanned!.RevokedAt);
        Assert.Equal(2, records.Reads);
    }

    [Fact]
    public async Task A_revoke_overtaken_by_a_scan_rereads_and_still_revokes_keeping_the_scan()
    {
        await IssueAsync("card-1");
        var records = new InterleavingRecords(Records(),
            () => Orchestrator(Records()).RecordScanAsync(Tenant, "card-1"));

        var revoked = await Orchestrator(records).RevokeAsync(Tenant, "card-1", "admin", Lost);

        Assert.NotNull(revoked!.RevokedAt);
        var stored = await ReadAsync("card-1");
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(1, stored.ScanCount);
        Assert.Equal(2, records.Reads);
    }

    [Fact]
    public async Task Two_concurrent_saves_from_the_same_read_one_wins_the_other_conflicts()
    {
        await IssueAsync("card-1");
        var a = await ReadAsync("card-1");
        var b = await ReadAsync("card-1");
        a.RevokedAt = DateTime.UtcNow;
        b.ScanCount = 7;

        var outcomes = await Task.WhenAll(Try(() => Records().UpsertAsync(a)), Try(() => Records().UpsertAsync(b)));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var conflict = Assert.Single(outcomes.OfType<IdCardConcurrencyException>());
        Assert.IsAssignableFrom<ConcurrencyConflictException>(conflict);
        var stored = await ReadAsync("card-1");
        Assert.Equal(2, stored.Revision);
        Assert.Equal(1, await _db.GetCollection<IdCardRecord>("idcard_records").CountDocumentsAsync(FilterDefinition<IdCardRecord>.Empty));
    }

    [Fact]
    public async Task A_stale_save_restores_the_in_memory_revision()
    {
        await IssueAsync("card-1");
        var stale = await ReadAsync("card-1");
        var fresh = await ReadAsync("card-1");
        await Records().UpsertAsync(fresh);

        await Assert.ThrowsAsync<IdCardConcurrencyException>(() => Records().UpsertAsync(stale));

        Assert.Equal(1, stale.Revision);
    }

    [Fact]
    public async Task A_card_written_before_the_revision_existed_saves_once_then_carries_one()
    {
        await IssueAsync("card-1");
        await _db.GetCollection<BsonDocument>("idcard_records").UpdateOneAsync(
            new BsonDocument("CardId", "card-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("card-1");
        var legacyToo = await ReadAsync("card-1");
        Assert.Equal(0, legacy.Revision);

        await Orchestrator(Records()).RevokeAsync(Tenant, "card-1", "admin", Lost);

        Assert.Equal(1, (await ReadAsync("card-1")).Revision);
        legacyToo.ScanCount = 1;
        await Assert.ThrowsAsync<IdCardConcurrencyException>(() => Records().UpsertAsync(legacyToo));
        Assert.NotNull((await ReadAsync("card-1")).RevokedAt);
    }

    [Fact]
    public async Task A_new_card_is_inserted_at_revision_one_and_a_missing_card_is_still_not_found()
    {
        var issued = await IssueAsync("card-new");

        Assert.Equal(1, issued.Revision);
        Assert.Equal(1, (await ReadAsync("card-new")).Revision);
        Assert.Null(await Orchestrator(Records()).RecordScanAsync(Tenant, "nope"));
        Assert.Null(await Orchestrator(Records()).RevokeAsync(Tenant, "nope", "admin", Lost));
    }

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
