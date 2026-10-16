using ClaimsService.Models;
using Microsoft.Azure.Cosmos;
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

public class ClaimAccessAuditRepositoryCosmos : IClaimAccessAuditRepository
{
    private readonly Container _container;

    public ClaimAccessAuditRepositoryCosmos(CosmosClient cosmosClient, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var containerName = configuration["CosmosDb:ClaimAccessAuditContainer"] ?? "ClaimAccessAudit";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public Task AppendAsync(ClaimAccessEvent accessEvent, CancellationToken ct = default) =>
        _container.CreateItemAsync(accessEvent, new PartitionKey(accessEvent.TenantId), cancellationToken: ct);

    public async Task<IReadOnlyList<ClaimAccessEvent>> QueryAsync(ClaimAccessQuery query, CancellationToken ct = default)
    {
        var where = new List<string> { "c.tenantId = @tenantId" };
        var definition = new Dictionary<string, object> { ["@tenantId"] = query.TenantId };
        if (!string.IsNullOrWhiteSpace(query.ClaimId))
        {
            where.Add("(c.claimId = @claimId OR ARRAY_CONTAINS(c.claimIds, @claimId))");
            definition["@claimId"] = query.ClaimId;
        }
        if (!string.IsNullOrWhiteSpace(query.MemberId))
        {
            where.Add("c.memberId = @memberId");
            definition["@memberId"] = query.MemberId;
        }
        if (!string.IsNullOrWhiteSpace(query.ActorId))
        {
            where.Add("c.actorId = @actorId");
            definition["@actorId"] = query.ActorId;
        }
        if (query.From is { } from)
        {
            where.Add("c.occurredAt >= @from");
            definition["@from"] = from;
        }
        if (query.To is { } to)
        {
            where.Add("c.occurredAt <= @to");
            definition["@to"] = to;
        }

        var limit = Math.Clamp(query.Limit, 1, 1000);
        var sql = new QueryDefinition($"SELECT TOP {limit} * FROM c WHERE {string.Join(" AND ", where)} ORDER BY c.occurredAt DESC");
        foreach (var (name, value) in definition) sql = sql.WithParameter(name, value);

        var iterator = _container.GetItemQueryIterator<ClaimAccessEvent>(
            sql, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(query.TenantId) });
        var results = new List<ClaimAccessEvent>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(ct));
        }
        return results;
    }
}
