using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// Append-only store for claim notes. There is deliberately no update or
/// delete: a note, once written, is part of the claim's record.
///
/// Tenant isolation: every read and write is scoped to the tenant.
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
