using System.Collections.Concurrent;
using MongoDB.Driver;
using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

public sealed class MongoWorkspaceStore : IWorkspaceStore
{
    public const string NoticesCollection = "PlanNotices";
    public const string ReadsCollection = "PlanNoticeReads";
    public const string PinsCollection = "PinnedRecords";

    private static readonly ConcurrentDictionary<string, bool> IndexedDatabases = new();

    private readonly IMongoCollection<PlanNotice> _notices;
    private readonly IMongoCollection<NoticeRead> _reads;
    private readonly IMongoCollection<PinnedRecord> _pins;

    public MongoWorkspaceStore(IMongoDatabase database)
    {
        _notices = database.GetCollection<PlanNotice>(NoticesCollection);
        _reads = database.GetCollection<NoticeRead>(ReadsCollection);
        _pins = database.GetCollection<PinnedRecord>(PinsCollection);
        if (IndexedDatabases.TryAdd(database.DatabaseNamespace.DatabaseName, true))
        {
            _notices.Indexes.CreateOne(new CreateIndexModel<PlanNotice>(Builders<PlanNotice>.IndexKeys.Ascending(n => n.TenantId)));
            _reads.Indexes.CreateOne(new CreateIndexModel<NoticeRead>(
                Builders<NoticeRead>.IndexKeys.Ascending(r => r.TenantId).Ascending(r => r.UserId)));
            _pins.Indexes.CreateOne(new CreateIndexModel<PinnedRecord>(
                Builders<PinnedRecord>.IndexKeys.Ascending(p => p.TenantId).Ascending(p => p.UserId)));
        }
    }

    public async Task<IReadOnlyList<PlanNotice>> ListNoticesAsync(string tenantId, CancellationToken ct = default) =>
        await _notices.Find(n => n.TenantId == tenantId).ToListAsync(ct);

    public async Task<PlanNotice?> GetNoticeAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _notices.Find(n => n.TenantId == tenantId && n.Id == id).FirstOrDefaultAsync(ct);

    public Task UpsertNoticeAsync(PlanNotice notice, CancellationToken ct = default) =>
        _notices.ReplaceOneAsync(n => n.TenantId == notice.TenantId && n.Id == notice.Id, notice, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlySet<string>> ReadNoticeIdsAsync(string tenantId, string userId, CancellationToken ct = default) =>
        (await _reads.Find(r => r.TenantId == tenantId && r.UserId == userId).Project(r => r.NoticeId).ToListAsync(ct)).ToHashSet();

    public Task MarkReadAsync(NoticeRead read, CancellationToken ct = default) =>
        _reads.ReplaceOneAsync(r => r.TenantId == read.TenantId && r.Id == read.Id, read, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<PinnedRecord>> ListPinsAsync(string tenantId, string userId, CancellationToken ct = default) =>
        await _pins.Find(p => p.TenantId == tenantId && p.UserId == userId).ToListAsync(ct);

    public Task UpsertPinAsync(PinnedRecord pin, CancellationToken ct = default) =>
        _pins.ReplaceOneAsync(p => p.TenantId == pin.TenantId && p.Id == pin.Id, pin, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<bool> DeletePinAsync(string tenantId, string id, CancellationToken ct = default) =>
        (await _pins.DeleteOneAsync(p => p.TenantId == tenantId && p.Id == id, ct)).DeletedCount > 0;
}
