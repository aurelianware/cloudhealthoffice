using MemberService.Models;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB repository for <see cref="MemberEvent"/>. Enforces idempotency via a
/// unique compound index on <c>(TenantId, MemberId, EventId)</c>.
/// </summary>
public class MemberEventRepositoryMongo : IMemberEventRepository
{
    private readonly IMongoCollection<MemberEvent> _collection;

    public MemberEventRepositoryMongo(IMongoDatabase database, string collectionName = "member-events")
    {
        _collection = database.GetCollection<MemberEvent>(collectionName);

        var idemKeys = Builders<MemberEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.MemberId)
            .Ascending(x => x.EventId);
        _collection.Indexes.CreateOne(new CreateIndexModel<MemberEvent>(
            idemKeys,
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_event" }));

        var orderKeys = Builders<MemberEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.MemberId)
            .Ascending(x => x.Version);
        _collection.Indexes.CreateOne(new CreateIndexModel<MemberEvent>(
            orderKeys,
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_version" }));
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

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = MemberEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
    }
}
