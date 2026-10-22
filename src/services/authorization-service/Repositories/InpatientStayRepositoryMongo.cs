using System.Collections.Concurrent;
using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using MongoDB.Driver;

namespace AuthorizationService.Repositories;

/// <summary>
/// MongoDB store for inpatient stays. Mirrors
/// <see cref="AuthorizationRepositoryMongo"/>: the tenant comes from the
/// request context and is part of every filter, and a whole-document replace
/// is fenced by the revision the caller read.
/// </summary>
public sealed class InpatientStayRepositoryMongo : IInpatientStayRepository
{
    private readonly IMongoCollection<InpatientStay> _collection;
    private readonly InpatientStayIndexes _indexes;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InpatientStayRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        InpatientStayIndexes? indexes = null)
    {
        var collectionName = configuration["MongoDb:InpatientStaysCollection"] ?? "InpatientStays";
        _collection = database.GetCollection<InpatientStay>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _indexes = indexes ?? new InpatientStayIndexes();
    }

    public Task EnsureIndexesAsync(CancellationToken ct = default) => _indexes.EnsureAsync(_collection, ct);

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    public async Task<InpatientStay?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<InpatientStay>.Filter.And(
            Builders<InpatientStay>.Filter.Eq(x => x.Id, id),
            Builders<InpatientStay>.Filter.Eq(x => x.TenantId, GetTenantId()));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public Task<IReadOnlyList<InpatientStay>> ListOpenAsync(string? facilityNpi = null, CancellationToken ct = default) =>
        ListOpenCoreAsync(GetTenantId(), facilityNpi, ct);

    public Task<IReadOnlyList<InpatientStay>> ListOpenForTenantAsync(string tenantId, CancellationToken ct = default) =>
        ListOpenCoreAsync(tenantId, facilityNpi: null, ct);

    private async Task<IReadOnlyList<InpatientStay>> ListOpenCoreAsync(
        string tenantId, string? facilityNpi, CancellationToken ct)
    {
        var builder = Builders<InpatientStay>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId)
                   & builder.Eq(x => x.Status, InpatientStayStatus.Admitted);

        if (!string.IsNullOrWhiteSpace(facilityNpi))
            filter &= builder.Eq(x => x.FacilityNPI, facilityNpi);

        return await _collection.Find(filter).SortBy(x => x.AdmissionDate).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<InpatientStay>> ListByAuthorizationAsync(
        string authorizationId, CancellationToken ct = default)
    {
        var filter = Builders<InpatientStay>.Filter.And(
            Builders<InpatientStay>.Filter.Eq(x => x.TenantId, GetTenantId()),
            Builders<InpatientStay>.Filter.Eq(x => x.AuthorizationId, authorizationId));
        return await _collection.Find(filter).SortBy(x => x.AdmissionDate).ToListAsync(ct);
    }

    public async Task<InpatientStay> CreateAsync(InpatientStay stay, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);

        stay.TenantId = GetTenantId();
        stay.Revision = 1;
        stay.CreatedDate = DateTime.UtcNow;
        stay.LastUpdatedDate = stay.CreatedDate;

        await _collection.InsertOneAsync(stay, cancellationToken: ct);
        return stay;
    }

    public async Task<InpatientStay> UpdateAsync(InpatientStay stay, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var expected = stay.Revision;

        stay.TenantId = tenantId;
        stay.Revision = expected + 1;
        stay.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<InpatientStay>.Filter.And(
            Builders<InpatientStay>.Filter.Eq(x => x.Id, stay.Id),
            Builders<InpatientStay>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<InpatientStay>.Filter.Eq(x => x.Revision, expected));

        var result = await _collection.ReplaceOneAsync(filter, stay, new ReplaceOptions { IsUpsert = false }, ct);

        if (result.MatchedCount == 0)
        {
            stay.Revision = expected;
            throw new InpatientStayConcurrencyException(stay.Id, expected);
        }

        return stay;
    }

    public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var tenants = await _collection.DistinctAsync(x => x.TenantId, FilterDefinition<InpatientStay>.Empty, cancellationToken: ct);
        return (await tenants.ToListAsync(ct)).Where(t => !string.IsNullOrEmpty(t)).ToList();
    }
}

/// <summary>One asynchronous index initialization per client/collection, shared by request scopes.</summary>
public sealed class InpatientStayIndexes
{
    private readonly ConcurrentDictionary<(IMongoClient Client, string Collection), Lazy<Task>> _ready = new();

    public async Task EnsureAsync(IMongoCollection<InpatientStay> collection, CancellationToken ct = default)
    {
        var key = (collection.Database.Client, collection.CollectionNamespace.FullName);
        var work = _ready.GetOrAdd(key, _ => new Lazy<Task>(() => CreateAsync(collection),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // A request cancelling must not cancel the initialization shared by other writers.
            await work.Value.WaitAsync(ct);
        }
        catch when (work.IsValueCreated && work.Value.IsFaulted)
        {
            ((ICollection<KeyValuePair<(IMongoClient, string), Lazy<Task>>>)_ready)
                .Remove(new KeyValuePair<(IMongoClient, string), Lazy<Task>>(key, work));
            throw;
        }
    }

    private static async Task CreateAsync(IMongoCollection<InpatientStay> collection)
    {
        var keys = Builders<InpatientStay>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            // The census: one tenant's open stays, by facility.
            new CreateIndexModel<InpatientStay>(
                keys.Ascending(s => s.TenantId).Ascending(s => s.Status).Ascending(s => s.FacilityNPI),
                new CreateIndexOptions { Name = "stay_tenant_status_facility" }),
            // The watchdog: open stays whose review date has passed.
            new CreateIndexModel<InpatientStay>(
                keys.Ascending(s => s.TenantId).Ascending(s => s.Status).Ascending(s => s.NextReviewDueDate),
                new CreateIndexOptions { Name = "stay_tenant_status_next_review" }),
            new CreateIndexModel<InpatientStay>(
                keys.Ascending(s => s.TenantId).Ascending(s => s.AuthorizationId),
                new CreateIndexOptions { Name = "stay_tenant_authorization" }),
        ]);
    }
}
