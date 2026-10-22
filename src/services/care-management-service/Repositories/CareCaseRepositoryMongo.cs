using System.Collections.Concurrent;
using CareManagementService.Models;
using MongoDB.Driver;

namespace CareManagementService.Repositories;

/// <summary>
/// MongoDB store for care cases. The tenant comes from the request context
/// and is part of every filter; a whole-document replace is fenced by the
/// revision the caller read.
/// </summary>
public sealed class CareCaseRepositoryMongo : ICareCaseRepository
{
    private readonly IMongoCollection<CareCase> _collection;
    private readonly CareCaseIndexes _indexes;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CareCaseRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        CareCaseIndexes? indexes = null)
    {
        var collectionName = configuration["MongoDb:CareCasesCollection"] ?? "CareCases";
        _collection = database.GetCollection<CareCase>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _indexes = indexes ?? new CareCaseIndexes();
    }

    public Task EnsureIndexesAsync(CancellationToken ct = default) => _indexes.EnsureAsync(_collection, ct);

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    private static FilterDefinitionBuilder<CareCase> Filter => Builders<CareCase>.Filter;

    private static readonly CareCaseStatus[] ClosedStatuses =
        [CareCaseStatus.Closed, CareCaseStatus.Declined];

    public async Task<CareCase?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.Id, id) & Filter.Eq(c => c.TenantId, GetTenantId()))
            .FirstOrDefaultAsync(ct);

    public async Task<CareCase?> GetByCaseNumberAsync(string caseNumber, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.CaseNumber, caseNumber) & Filter.Eq(c => c.TenantId, GetTenantId()))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CareCase>> ListByMemberAsync(string memberId, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.TenantId, GetTenantId()) & Filter.Eq(c => c.MemberId, memberId))
            .SortByDescending(c => c.ReferredDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CareCase>> ListOpenAsync(
        string? careManagerId = null, CareProgram? program = null, int limit = 200, CancellationToken ct = default)
    {
        var filter = Filter.Eq(c => c.TenantId, GetTenantId())
                   & Filter.Nin(c => c.Status, ClosedStatuses);

        if (!string.IsNullOrWhiteSpace(careManagerId))
            filter &= Filter.Eq(c => c.CareManagerId, careManagerId);

        if (program is { } p)
            filter &= Filter.Eq(c => c.Program, p);

        return await _collection.Find(filter)
            .SortByDescending(c => c.Acuity)
            .ThenBy(c => c.NextContactDueDate)
            .Limit(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CareCase>> ListContactOverdueAsync(
        DateTime asOfUtc, CancellationToken ct = default) =>
        await _collection
            .Find(Filter.Eq(c => c.TenantId, GetTenantId())
                & Filter.Nin(c => c.Status, ClosedStatuses)
                & Filter.Lt(c => c.NextContactDueDate, asOfUtc.Date))
            .SortBy(c => c.NextContactDueDate)
            .ToListAsync(ct);

    public async Task<CareCase> CreateAsync(CareCase careCase, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);

        careCase.TenantId = GetTenantId();
        careCase.Revision = 1;
        careCase.CreatedDate = DateTime.UtcNow;
        careCase.LastUpdatedDate = careCase.CreatedDate;

        try
        {
            await _collection.InsertOneAsync(careCase, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateCaseNumberException(careCase.CaseNumber);
        }

        return careCase;
    }

    public async Task<CareCase> UpdateAsync(CareCase careCase, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var expected = careCase.Revision;

        careCase.TenantId = tenantId;
        careCase.Revision = expected + 1;
        careCase.LastUpdatedDate = DateTime.UtcNow;

        var result = await _collection.ReplaceOneAsync(
            Filter.Eq(c => c.Id, careCase.Id)
                & Filter.Eq(c => c.TenantId, tenantId)
                & Filter.Eq(c => c.Revision, expected),
            careCase,
            new ReplaceOptions { IsUpsert = false },
            ct);

        if (result.MatchedCount == 0)
        {
            careCase.Revision = expected;
            throw new CareCaseConcurrencyException(careCase.Id, expected);
        }

        return careCase;
    }
}

/// <summary>One asynchronous index initialization per client/collection, shared by request scopes.</summary>
public sealed class CareCaseIndexes
{
    private readonly ConcurrentDictionary<(IMongoClient Client, string Collection), Lazy<Task>> _ready = new();

    public async Task EnsureAsync(IMongoCollection<CareCase> collection, CancellationToken ct = default)
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

    private static async Task CreateAsync(IMongoCollection<CareCase> collection)
    {
        var keys = Builders<CareCase>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            new CreateIndexModel<CareCase>(keys.Ascending(c => c.TenantId).Ascending(c => c.CaseNumber),
                new CreateIndexOptions { Name = "care_case_tenant_number_unique", Unique = true }),
            new CreateIndexModel<CareCase>(keys.Ascending(c => c.TenantId).Ascending(c => c.MemberId),
                new CreateIndexOptions { Name = "care_case_tenant_member" }),
            // The caseload: one care manager's open cases.
            new CreateIndexModel<CareCase>(
                keys.Ascending(c => c.TenantId).Ascending(c => c.Status).Ascending(c => c.CareManagerId),
                new CreateIndexOptions { Name = "care_case_tenant_status_manager" }),
            // The overdue list.
            new CreateIndexModel<CareCase>(
                keys.Ascending(c => c.TenantId).Ascending(c => c.Status).Ascending(c => c.NextContactDueDate),
                new CreateIndexOptions { Name = "care_case_tenant_status_next_contact" }),
        ]);
    }
}
