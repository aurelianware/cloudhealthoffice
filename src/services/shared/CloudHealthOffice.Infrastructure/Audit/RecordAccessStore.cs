using System.Collections.Concurrent;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Audit;

/// <summary>Append-only store of record access events. No update or delete: the log is evidence.</summary>
public interface IRecordAccessStore
{
    Task AppendAsync(RecordAccessEvent accessEvent, CancellationToken ct = default);

    /// <summary>Newest first. A record id matches single-record reads of it and searches that returned it.</summary>
    Task<IReadOnlyList<RecordAccessEvent>> QueryAsync(RecordAccessQuery query, CancellationToken ct = default);
}

public sealed class RecordAccessStoreMongo : IRecordAccessStore
{
    public const string CollectionName = "RecordAccessAudit";

    private static readonly ConcurrentDictionary<string, bool> Indexed = new();
    private readonly IMongoCollection<RecordAccessEvent> _collection;

    public RecordAccessStoreMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<RecordAccessEvent>(CollectionName);
        var name = database.DatabaseNamespace.DatabaseName;
        if (Indexed.TryAdd(name, true))
        {
            // In the background and best effort: an unreachable or read-only
            // database must never hold up (or fail) the first read.
            _ = Task.Run(async () =>
            {
                var keys = Builders<RecordAccessEvent>.IndexKeys;
                try
                {
                    await _collection.Indexes.CreateManyAsync(
                    [
                        new CreateIndexModel<RecordAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.RecordId).Descending(e => e.OccurredAt)),
                        new CreateIndexModel<RecordAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.RecordIds).Descending(e => e.OccurredAt)),
                        new CreateIndexModel<RecordAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.MemberId).Descending(e => e.OccurredAt)),
                        new CreateIndexModel<RecordAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.ActorId).Descending(e => e.OccurredAt)),
                    ]);
                }
                catch (Exception)
                {
                    // Retried by the next store instance (next process start).
                    Indexed.TryRemove(name, out _);
                }
            });
        }
    }

    public Task AppendAsync(RecordAccessEvent accessEvent, CancellationToken ct = default) =>
        _collection.InsertOneAsync(accessEvent, cancellationToken: ct);

    public async Task<IReadOnlyList<RecordAccessEvent>> QueryAsync(RecordAccessQuery q, CancellationToken ct = default)
    {
        var f = Builders<RecordAccessEvent>.Filter;
        var filter = f.Eq(e => e.TenantId, q.TenantId);
        if (!string.IsNullOrWhiteSpace(q.RecordType)) filter &= f.Eq(e => e.RecordType, q.RecordType);
        if (!string.IsNullOrWhiteSpace(q.RecordId)) filter &= f.Or(f.Eq(e => e.RecordId, q.RecordId), f.AnyEq(e => e.RecordIds, q.RecordId));
        if (!string.IsNullOrWhiteSpace(q.MemberId)) filter &= f.Eq(e => e.MemberId, q.MemberId);
        if (!string.IsNullOrWhiteSpace(q.ActorId)) filter &= f.Eq(e => e.ActorId, q.ActorId);
        if (q.From is { } from) filter &= f.Gte(e => e.OccurredAt, from);
        if (q.To is { } to) filter &= f.Lte(e => e.OccurredAt, to);
        return await _collection.Find(filter).SortByDescending(e => e.OccurredAt).Limit(Math.Clamp(q.Limit, 1, 1000)).ToListAsync(ct);
    }
}

/// <summary>For development hosts with no database, and for tests.</summary>
public sealed class RecordAccessStoreInMemory : IRecordAccessStore
{
    private readonly ConcurrentQueue<RecordAccessEvent> _events = new();

    /// <summary>Simulate an outage: every append throws.</summary>
    public bool Fail { get; set; }

    public IReadOnlyCollection<RecordAccessEvent> All => _events.ToArray();

    public void Clear() => _events.Clear();

    public Task AppendAsync(RecordAccessEvent accessEvent, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("record access store down");
        _events.Enqueue(accessEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecordAccessEvent>> QueryAsync(RecordAccessQuery q, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecordAccessEvent>>(_events
            .Where(e => e.TenantId == q.TenantId)
            .Where(e => q.RecordType is null || e.RecordType == q.RecordType)
            .Where(e => q.RecordId is null || e.RecordId == q.RecordId || e.RecordIds.Contains(q.RecordId))
            .Where(e => q.MemberId is null || e.MemberId == q.MemberId)
            .Where(e => q.ActorId is null || e.ActorId == q.ActorId)
            .Where(e => q.From is null || e.OccurredAt >= q.From)
            .Where(e => q.To is null || e.OccurredAt <= q.To)
            .OrderByDescending(e => e.OccurredAt)
            .Take(Math.Clamp(q.Limit, 1, 1000))
            .ToList());
}
