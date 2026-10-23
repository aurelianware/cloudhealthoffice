using System.Collections.Concurrent;
using ContactService.Models;
using MongoDB.Driver;

namespace ContactService.Repositories;

/// <summary>MongoDB store for the tenant's reason taxonomy.</summary>
public sealed class ContactReasonRepositoryMongo : IContactReasonRepository
{
    private readonly IMongoCollection<ContactReason> _collection;
    private readonly ContactReasonIndexes _indexes;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ContactReasonRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ContactReasonIndexes? indexes = null)
    {
        var collectionName = configuration["MongoDb:ContactReasonsCollection"] ?? "ContactReasons";
        _collection = database.GetCollection<ContactReason>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _indexes = indexes ?? new ContactReasonIndexes();
    }

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    private static FilterDefinitionBuilder<ContactReason> Filter => Builders<ContactReason>.Filter;

    public async Task<IReadOnlyList<ContactReason>> ListAsync(
        bool activeOnly = false, CancellationToken ct = default)
    {
        var filter = Filter.Eq(r => r.TenantId, GetTenantId());
        if (activeOnly) filter &= Filter.Eq(r => r.IsActive, true);

        return await _collection.Find(filter)
            .SortByDescending(r => r.IsActive)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Order)
            .ThenBy(r => r.Label)
            .ToListAsync(ct);
    }

    public async Task<ContactReason?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(r => r.TenantId, GetTenantId()) & Filter.Eq(r => r.Code, Normalize(code)))
            .FirstOrDefaultAsync(ct);

    public async Task<ContactReason> SaveAsync(ContactReason reason, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        await _indexes.EnsureAsync(_collection, ct);

        reason.TenantId = tenantId;
        reason.Code = Normalize(reason.Code);
        reason.UpdatedAt = DateTime.UtcNow;

        var existing = await _collection
            .Find(Filter.Eq(r => r.TenantId, tenantId) & Filter.Eq(r => r.Code, reason.Code))
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            reason.CreatedAt = reason.UpdatedAt;
            await _collection.InsertOneAsync(reason, cancellationToken: ct);
            return reason;
        }

        // Keep the identity and the creation time of the reason that is
        // already on contacts; only its wording and placing change.
        reason.Id = existing.Id;
        reason.CreatedAt = existing.CreatedAt;
        reason.SystemKey = existing.SystemKey;

        await _collection.ReplaceOneAsync(
            Filter.Eq(r => r.Id, existing.Id) & Filter.Eq(r => r.TenantId, tenantId),
            reason,
            cancellationToken: ct);

        return reason;
    }

    public async Task<int> SeedStarterReasonsAsync(CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        await _indexes.EnsureAsync(_collection, ct);

        if (await _collection.Find(Filter.Eq(r => r.TenantId, tenantId)).AnyAsync(ct)) return 0;

        var now = DateTime.UtcNow;
        var reasons = StarterContactReasons.All.Select((seed, index) => new ContactReason
        {
            TenantId = tenantId,
            Code = seed.Code,
            Label = seed.Label,
            Category = seed.Category,
            Order = index,
            IsActive = true,
            IsComplaintIndicator = seed.IsComplaintIndicator,
            SystemKey = seed.Code,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();

        try
        {
            await _collection.InsertManyAsync(reasons, cancellationToken: ct);
        }
        catch (MongoBulkWriteException ex)
            when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // Another host seeded the same tenant at the same moment. The
            // taxonomy is there either way, which is all the caller wanted.
            return 0;
        }

        return reasons.Count;
    }

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();
}

/// <summary>Creates the reason indexes once per collection.</summary>
public sealed class ContactReasonIndexes
{
    private readonly ConcurrentDictionary<(IMongoClient Client, string Collection), Lazy<Task>> _ready = new();

    public async Task EnsureAsync(IMongoCollection<ContactReason> collection, CancellationToken ct = default)
    {
        var key = (collection.Database.Client, collection.CollectionNamespace.FullName);
        var work = _ready.GetOrAdd(key, _ => new Lazy<Task>(() => CreateAsync(collection),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            await work.Value.WaitAsync(ct);
        }
        catch when (work.IsValueCreated && work.Value.IsFaulted)
        {
            ((ICollection<KeyValuePair<(IMongoClient, string), Lazy<Task>>>)_ready)
                .Remove(new KeyValuePair<(IMongoClient, string), Lazy<Task>>(key, work));
            throw;
        }
    }

    private static Task CreateAsync(IMongoCollection<ContactReason> collection) =>
        collection.Indexes.CreateOneAsync(new CreateIndexModel<ContactReason>(
            Builders<ContactReason>.IndexKeys.Ascending(r => r.TenantId).Ascending(r => r.Code),
            new CreateIndexOptions { Name = "contact_reason_tenant_code_unique", Unique = true }));
}
