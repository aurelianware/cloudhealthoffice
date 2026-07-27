using MemberService.Models;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB repository for <see cref="MemberEvent"/>. Idempotency and ordering
/// are enforced by unique compound indexes created at startup by
/// <c>MemberEventIndexInitializer</c> (not here — keeping construction
/// side-effect free so the repository can be registered as a singleton).
/// </summary>
public class MemberEventRepositoryMongo : IMemberEventRepository
{
    private readonly IMongoCollection<MemberEvent> _collection;

    public MemberEventRepositoryMongo(IMongoDatabase database, string collectionName = "member-events")
    {
        _collection = database.GetCollection<MemberEvent>(collectionName);
    }

    public async Task<AppendResult> AppendAsync(MemberEvent evt, CancellationToken ct = default)
    {
        NormalizeEnvelope(evt);

        try
        {
            await _collection.InsertOneAsync(evt, cancellationToken: ct);
            return new AppendResult(evt, Appended: true);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            return new AppendResult(existing ?? evt, Appended: false);
        }
    }

    public async Task<IReadOnlyList<MemberEvent>> ListByMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var filter = Builders<MemberEvent>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<MemberEvent>.Filter.Eq(x => x.MemberId, memberId);
        return await _collection.Find(filter)
            .SortBy(x => x.Version)
            .ToListAsync(ct);
    }

    public async Task<MemberEvent?> GetByIdAsync(
        string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        var filter = Builders<MemberEvent>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<MemberEvent>.Filter.Eq(x => x.MemberId, memberId) &
                     Builders<MemberEvent>.Filter.Eq(x => x.EventId, eventId);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetNextVersionAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var filter = Builders<MemberEvent>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<MemberEvent>.Filter.Eq(x => x.MemberId, memberId);
        var last = await _collection.Find(filter)
            .SortByDescending(x => x.Version)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        return (last?.Version ?? 0) + 1;
    }

    private static void NormalizeEnvelope(MemberEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.MemberId))
            throw new ArgumentException("TenantId and MemberId are required");

        // Mongo's _id index is global to the collection, while EventId is only
        // unique within (TenantId, MemberId). Always replace the publisher's
        // Cosmos-compatible Id so the same idempotency key can be reused safely
        // by another tenant or member.
        evt.Id = MemberEvent.BuildMongoDocumentId(evt.TenantId, evt.MemberId, evt.EventId);
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = MemberEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
    }
}
