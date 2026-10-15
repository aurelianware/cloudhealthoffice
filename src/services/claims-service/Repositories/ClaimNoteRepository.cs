using ClaimsService.Models;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// Append-only store for claim notes. There is deliberately no update or
/// delete: a note, once written, is part of the claim's record.
///
/// Tenant isolation: every read and write is scoped to the tenant. The Cosmos
/// implementation uses TenantId as the partition key.
/// </summary>
public interface IClaimNoteRepository
{
    Task<ClaimNote> AppendAsync(ClaimNote note, CancellationToken ct = default);

    /// <summary>The claim's notes, oldest first (the order they were written).</summary>
    Task<IReadOnlyList<ClaimNote>> ListByClaimAsync(string claimId, string tenantId, CancellationToken ct = default);
}

public class ClaimNoteRepositoryMongo : IClaimNoteRepository
{
    public const string CollectionName = "ClaimNotes";

    private readonly IMongoCollection<ClaimNote> _collection;

    public ClaimNoteRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<ClaimNote>(CollectionName);
    }

    /// <summary>The one index the read path needs; created at startup.</summary>
    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
    {
        var keys = Builders<ClaimNote>.IndexKeys;
        database.GetCollection<ClaimNote>(CollectionName).Indexes.CreateOne(
            new CreateIndexModel<ClaimNote>(
                keys.Ascending(n => n.TenantId).Ascending(n => n.ClaimId).Ascending(n => n.CreatedAt)),
            cancellationToken: ct);
    }

    public async Task<ClaimNote> AppendAsync(ClaimNote note, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString();
        if (note.CreatedAt == default) note.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(note, cancellationToken: ct);
        return note;
    }

    public async Task<IReadOnlyList<ClaimNote>> ListByClaimAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var filter = Builders<ClaimNote>.Filter.And(
            Builders<ClaimNote>.Filter.Eq(n => n.TenantId, tenantId),
            Builders<ClaimNote>.Filter.Eq(n => n.ClaimId, claimId));
        return await _collection.Find(filter).SortBy(n => n.CreatedAt).ToListAsync(ct);
    }
}

public class ClaimNoteRepositoryCosmos : IClaimNoteRepository
{
    private readonly Container _container;

    public ClaimNoteRepositoryCosmos(CosmosClient cosmosClient, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var containerName = configuration["CosmosDb:ClaimNotesContainer"] ?? "ClaimNotes";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task<ClaimNote> AppendAsync(ClaimNote note, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString();
        if (note.CreatedAt == default) note.CreatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(note, new PartitionKey(note.TenantId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<IReadOnlyList<ClaimNote>> ListByClaimAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.createdAt ASC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _container.GetItemQueryIterator<ClaimNote>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<ClaimNote>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(ct));
        }
        return results;
    }
}
