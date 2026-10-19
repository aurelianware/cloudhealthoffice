using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// Append-only store for claim access events. No update or delete: the log
/// is evidence. Every query is scoped to the tenant.
/// </summary>
public interface IClaimAccessAuditRepository
{
    Task AppendAsync(ClaimAccessEvent accessEvent, CancellationToken ct = default);

    /// <summary>Newest first. A claim matches single-claim reads of it and searches that returned it.</summary>
    Task<IReadOnlyList<ClaimAccessEvent>> QueryAsync(ClaimAccessQuery query, CancellationToken ct = default);
}

public class ClaimAccessAuditRepositoryMongo : IClaimAccessAuditRepository
{
    public const string CollectionName = "ClaimAccessAudit";

    private readonly IMongoCollection<ClaimAccessEvent> _collection;

    public ClaimAccessAuditRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<ClaimAccessEvent>(CollectionName);
    }

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
    {
        var keys = Builders<ClaimAccessEvent>.IndexKeys;
        database.GetCollection<ClaimAccessEvent>(CollectionName).Indexes.CreateMany(
        [
            new CreateIndexModel<ClaimAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.ClaimId).Descending(e => e.OccurredAt)),
            new CreateIndexModel<ClaimAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.ClaimIds).Descending(e => e.OccurredAt)),
            new CreateIndexModel<ClaimAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.MemberId).Descending(e => e.OccurredAt)),
            new CreateIndexModel<ClaimAccessEvent>(keys.Ascending(e => e.TenantId).Ascending(e => e.ActorId).Descending(e => e.OccurredAt)),
        ], ct);
    }

    public Task AppendAsync(ClaimAccessEvent accessEvent, CancellationToken ct = default) =>
        _collection.InsertOneAsync(accessEvent, cancellationToken: ct);

    public async Task<IReadOnlyList<ClaimAccessEvent>> QueryAsync(ClaimAccessQuery query, CancellationToken ct = default)
    {
        var f = Builders<ClaimAccessEvent>.Filter;
        var filter = f.Eq(e => e.TenantId, query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.ClaimId))
            filter &= f.Or(f.Eq(e => e.ClaimId, query.ClaimId), f.AnyEq(e => e.ClaimIds, query.ClaimId));
        if (!string.IsNullOrWhiteSpace(query.MemberId))
            filter &= f.Eq(e => e.MemberId, query.MemberId);
        if (!string.IsNullOrWhiteSpace(query.ActorId))
            filter &= f.Eq(e => e.ActorId, query.ActorId);
        if (query.From is { } from)
            filter &= f.Gte(e => e.OccurredAt, from);
        if (query.To is { } to)
            filter &= f.Lte(e => e.OccurredAt, to);

        return await _collection.Find(filter)
            .SortByDescending(e => e.OccurredAt)
            .Limit(Math.Clamp(query.Limit, 1, 1000))
            .ToListAsync(ct);
    }
}
