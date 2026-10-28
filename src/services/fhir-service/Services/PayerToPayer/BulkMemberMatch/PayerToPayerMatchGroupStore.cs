using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.PayerToPayer.BulkMemberMatch;

/// <summary>
/// One Group a PDex <c>$bulk-member-match</c> produced: the matched members,
/// those not matched, or those matched whose consent could not be honoured.
/// It belongs to the requesting payer, and to the client that asked, and to
/// nobody else.
/// </summary>
public sealed class PayerToPayerMatchGroup
{
    public required string TenantId { get; init; }
    public required string GroupId { get; init; }

    /// <summary>The <c>$bulk-member-match</c> request the Group answers; its Groups share it.</summary>
    public required string RequestId { get; init; }

    /// <summary><c>match</c>, <c>nomatch</c> or <c>consentconstraint</c> (PDex PdexMultiMemberMatchResultCS).</summary>
    public required string Code { get; init; }

    public required string ReceivingPayerId { get; init; }

    /// <summary>The client that asked for the match.</summary>
    public required string RequestedBy { get; init; }

    /// <summary>CHO member ids, for the matched Group only: the members its export covers.</summary>
    public IReadOnlyList<string> MemberIds { get; init; } = [];

    /// <summary>The Group resource as it was answered, as FHIR JSON.</summary>
    public required string Resource { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The Groups <c>$bulk-member-match</c> answered with, kept so the requesting
/// payer can read them again and export the matched one
/// (<c>Group/{id}/$davinci-data-export</c>) until they expire.
/// </summary>
public interface IPayerToPayerMatchGroupStore
{
    Task SaveAsync(IReadOnlyList<PayerToPayerMatchGroup> groups, CancellationToken ct = default);

    /// <summary>The Group, while it has not expired at <paramref name="nowUtc"/>.</summary>
    Task<PayerToPayerMatchGroup?> GetAsync(string tenantId, string groupId, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>The Groups one request produced, while they have not expired.</summary>
    Task<IReadOnlyList<PayerToPayerMatchGroup>> ListForRequestAsync(
        string tenantId, string requestId, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// MongoDB store (<c>p2p_member_match_groups</c>). Expired Groups are removed
/// by a TTL index and are never answered before that removal runs. Singleton:
/// indexes are ensured once at construction.
/// </summary>
public sealed class MongoPayerToPayerMatchGroupStore : IPayerToPayerMatchGroupStore
{
    public const string CollectionName = "p2p_member_match_groups";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoPayerToPayerMatchGroupStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("groupId"),
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_group" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("requestId"),
                new CreateIndexOptions { Name = "ix_tenant_request" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_expiresAt" }),
        ]);
    }

    public async Task SaveAsync(IReadOnlyList<PayerToPayerMatchGroup> groups, CancellationToken ct = default)
    {
        if (groups.Count > 0)
            await _collection.InsertManyAsync(groups.Select(ToDocument), cancellationToken: ct);
    }

    public async Task<PayerToPayerMatchGroup?> GetAsync(
        string tenantId, string groupId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
                     & Builders<BsonDocument>.Filter.Eq("groupId", groupId)
                     & Builders<BsonDocument>.Filter.Gt("expiresAt", nowUtc.UtcDateTime);
        var document = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return document is null ? null : FromDocument(document);
    }

    public async Task<IReadOnlyList<PayerToPayerMatchGroup>> ListForRequestAsync(
        string tenantId, string requestId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
                     & Builders<BsonDocument>.Filter.Eq("requestId", requestId)
                     & Builders<BsonDocument>.Filter.Gt("expiresAt", nowUtc.UtcDateTime);
        var documents = await _collection.Find(filter).ToListAsync(ct);
        return documents.Select(FromDocument).ToList();
    }

    private static BsonDocument ToDocument(PayerToPayerMatchGroup group) => new()
    {
        ["tenantId"] = group.TenantId,
        ["groupId"] = group.GroupId,
        ["requestId"] = group.RequestId,
        ["code"] = group.Code,
        ["receivingPayerId"] = group.ReceivingPayerId,
        ["requestedBy"] = group.RequestedBy,
        ["memberIds"] = new BsonArray(group.MemberIds),
        ["resource"] = group.Resource,
        ["createdAt"] = group.CreatedAt.UtcDateTime,
        ["expiresAt"] = group.ExpiresAt.UtcDateTime,
    };

    private static PayerToPayerMatchGroup FromDocument(BsonDocument document) => new()
    {
        TenantId = document["tenantId"].AsString,
        GroupId = document["groupId"].AsString,
        RequestId = document["requestId"].AsString,
        Code = document["code"].AsString,
        ReceivingPayerId = document["receivingPayerId"].AsString,
        RequestedBy = document["requestedBy"].AsString,
        MemberIds = document["memberIds"].AsBsonArray.Select(v => v.AsString).ToList(),
        Resource = document["resource"].AsString,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(document["createdAt"].ToUniversalTime(), DateTimeKind.Utc)),
        ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(document["expiresAt"].ToUniversalTime(), DateTimeKind.Utc)),
    };
}

/// <summary>In-memory store, for tests and for hosts without MongoDB.</summary>
public sealed class InMemoryPayerToPayerMatchGroupStore : IPayerToPayerMatchGroupStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Group), PayerToPayerMatchGroup> _groups = new();

    public Task SaveAsync(IReadOnlyList<PayerToPayerMatchGroup> groups, CancellationToken ct = default)
    {
        foreach (var group in groups)
            _groups[(group.TenantId, group.GroupId)] = group;
        return Task.CompletedTask;
    }

    public Task<PayerToPayerMatchGroup?> GetAsync(
        string tenantId, string groupId, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Task.FromResult(_groups.TryGetValue((tenantId, groupId), out var group) && group.ExpiresAt > nowUtc
            ? group
            : null);

    public Task<IReadOnlyList<PayerToPayerMatchGroup>> ListForRequestAsync(
        string tenantId, string requestId, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PayerToPayerMatchGroup>>(_groups.Values
            .Where(g => g.TenantId == tenantId && g.RequestId == requestId && g.ExpiresAt > nowUtc)
            .ToList());
}
