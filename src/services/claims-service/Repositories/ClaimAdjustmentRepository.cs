using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// Persistence surface for the <see cref="ClaimAdjustment"/> aggregate
/// (capability 5.12). Mongo-only with a Cosmos noop fallback per Gap 4
/// ratification — adjustment rows are operational state best fit by
/// Mongo's append/query semantics, not domain snapshots. The Cosmos noop
/// throws on writes so deployments pinned to Cosmos surface the missing
/// capability immediately rather than silently dropping audit data.
///
/// <para>
/// Uniqueness key (Gap 5 ratification): <c>(TenantId, ClaimVersionId)</c>
/// — at most one in-flight adjustment per claim chain. Naturally enforces
/// Decision 11's depth=1 invariant in Phase 1; forward-compat with
/// depth&gt;1 by widening the key with a generation field in Phase 2.
/// </para>
///
/// <para>
/// Idempotency on <see cref="ClaimAdjustment.IdempotencyKey"/> is enforced
/// in the service layer (not the repository) via
/// <see cref="GetByIdempotencyKeyAsync"/> — same key + same hash returns
/// the existing row; same key + different hash returns 409.
/// </para>
/// </summary>
public interface IClaimAdjustmentRepository
{
    /// <summary>Insert a new ClaimAdjustment row. Throws on uniqueness collision.</summary>
    Task<ClaimAdjustment> CreateAsync(ClaimAdjustment adjustment, CancellationToken ct = default);

    Task<ClaimAdjustment?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>
    /// Look up by chain key — returns the in-flight (depth-1 invariant)
    /// adjustment for the chain if any. Used by the service layer to enforce
    /// Decision 11 (no concurrent adjustments per chain).
    /// </summary>
    Task<ClaimAdjustment?> GetByClaimVersionIdAsync(string tenantId, string claimVersionId, CancellationToken ct = default);

    /// <summary>
    /// Look up by operator-supplied idempotency key (Decision 6). Returns the
    /// existing adjustment row if any; service layer compares
    /// <see cref="ClaimAdjustment.RequestHash"/> to detect 409 cases.
    /// </summary>
    Task<ClaimAdjustment?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Look up by the new (replacement) claim row id (5.12b
    /// orchestrator-finalize callback). Returns the in-flight adjustment
    /// whose <see cref="ClaimAdjustment.NewClaimId"/> matches. Used by
    /// <see cref="IClaimAdjustmentService.OnNewVersionFinalizedAsync"/>
    /// to transition <c>AwaitingReadjudication → PendingReversal/Failed</c>.
    /// </summary>
    Task<ClaimAdjustment?> GetByNewClaimIdAsync(string tenantId, string newClaimId, CancellationToken ct = default);

    /// <summary>
    /// Look up by predecessor claim id + status (5.12b reversal-completion
    /// callback). Returns the adjustment whose
    /// <see cref="ClaimAdjustment.PredecessorClaimId"/> matches and whose
    /// <see cref="ClaimAdjustment.Status"/> equals the supplied filter.
    /// Used by <see cref="IClaimAdjustmentService.MarkActiveOnReversalAsync"/>
    /// to transition <c>PendingReversal → Active</c> on void success.
    /// </summary>
    Task<ClaimAdjustment?> GetByPredecessorAndStatusAsync(
        string tenantId,
        string predecessorClaimId,
        ClaimAdjustmentStatus status,
        CancellationToken ct = default);

    /// <summary>
    /// Filter + paginate. Filters not supplied are not applied. Returns
    /// (page, totalCount). The 5.12b ReversalRunService consumes this via
    /// the controller's HTTP surface to batch <c>PendingReversal</c> rows.
    /// </summary>
    Task<(IReadOnlyList<ClaimAdjustment> Page, int TotalCount)> ListAsync(
        string tenantId, ClaimAdjustmentListFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Mutates an existing adjustment row (e.g. status transitions, reversal-run linkage).
    /// Throws when the row is missing. Does not enforce a state-machine guard at
    /// the repository layer — the service layer owns the lifecycle ordering.
    /// </summary>
    Task<ClaimAdjustment> UpdateAsync(ClaimAdjustment adjustment, CancellationToken ct = default);

    /// <summary>
    /// Delete an adjustment row. Used by the service layer to release the
    /// chain-uniqueness lock when an early-stage failure leaves a placeholder
    /// row blocking the chain (e.g. submission rejected, supersession write
    /// failed before any version events fired). Returns true if a row was
    /// removed; false if no row matched.
    /// </summary>
    Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class ClaimAdjustmentRepositoryMongo : IClaimAdjustmentRepository
{
    private readonly IMongoCollection<ClaimAdjustment> _collection;

    public ClaimAdjustmentRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:ClaimAdjustmentsContainer"] ?? "ClaimAdjustments";
        _collection = database.GetCollection<ClaimAdjustment>(collectionName);
        // Index creation moved to ClaimAdjustmentIndexInitializer hosted
        // service so scoped repository resolution stays side-effect free.
    }

    public async Task<ClaimAdjustment> CreateAsync(ClaimAdjustment adjustment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        if (string.IsNullOrEmpty(adjustment.Id))
        {
            adjustment.Id = Guid.NewGuid().ToString();
        }
        await _collection.InsertOneAsync(adjustment, cancellationToken: ct);
        return adjustment;
    }

    public async Task<ClaimAdjustment?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.Id, id));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<ClaimAdjustment?> GetByClaimVersionIdAsync(string tenantId, string claimVersionId, CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ClaimVersionId, claimVersionId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<ClaimAdjustment?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.IdempotencyKey, idempotencyKey));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<ClaimAdjustment?> GetByNewClaimIdAsync(string tenantId, string newClaimId, CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.NewClaimId, newClaimId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<ClaimAdjustment?> GetByPredecessorAndStatusAsync(
        string tenantId,
        string predecessorClaimId,
        ClaimAdjustmentStatus status,
        CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.PredecessorClaimId, predecessorClaimId),
            b.Eq(x => x.Status, status));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<(IReadOnlyList<ClaimAdjustment> Page, int TotalCount)> ListAsync(
        string tenantId, ClaimAdjustmentListFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var b = Builders<ClaimAdjustment>.Filter;
        var clauses = new List<FilterDefinition<ClaimAdjustment>> { b.Eq(x => x.TenantId, tenantId) };
        if (filter.Status.HasValue) clauses.Add(b.Eq(x => x.Status, filter.Status.Value));
        if (!string.IsNullOrEmpty(filter.PredecessorClaimId)) clauses.Add(b.Eq(x => x.PredecessorClaimId, filter.PredecessorClaimId));
        if (!string.IsNullOrEmpty(filter.CreatedBy)) clauses.Add(b.Eq(x => x.CreatedBy, filter.CreatedBy));
        if (filter.CreatedFrom.HasValue) clauses.Add(b.Gte(x => x.CreatedAt, filter.CreatedFrom.Value));
        if (filter.CreatedTo.HasValue) clauses.Add(b.Lte(x => x.CreatedAt, filter.CreatedTo.Value));

        var combined = b.And(clauses);
        var total = (int)await _collection.CountDocumentsAsync(combined, cancellationToken: ct);
        var page = await _collection.Find(combined)
            .SortByDescending(x => x.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Limit(filter.PageSize)
            .ToListAsync(ct);
        return (page, total);
    }

    public async Task<ClaimAdjustment> UpdateAsync(ClaimAdjustment adjustment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, adjustment.TenantId), b.Eq(x => x.Id, adjustment.Id));
        var result = await _collection.ReplaceOneAsync(filter, adjustment, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException(
                $"ClaimAdjustment {adjustment.Id} for tenant {adjustment.TenantId} not found for update.");
        }
        return adjustment;
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<ClaimAdjustment>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.Id, id));
        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }
}

/// <summary>
/// Cosmos-environment fallback. Per Gap 4 ratification, ClaimAdjustment
/// rows are Mongo-only in Phase 1 — the Cosmos write path is not
/// implemented. This impl throws on writes (so a Cosmos-only deployment
/// fails loudly the moment an operator submits an adjustment, rather
/// than silently dropping audit data) and returns null/empty on reads
/// (consistent with the no-data-here contract).
/// </summary>
public sealed class ClaimAdjustmentRepositoryCosmosNoop : IClaimAdjustmentRepository
{
    private readonly ILogger<ClaimAdjustmentRepositoryCosmosNoop> _logger;

    public ClaimAdjustmentRepositoryCosmosNoop(ILogger<ClaimAdjustmentRepositoryCosmosNoop> logger)
        => _logger = logger;

    public Task<ClaimAdjustment> CreateAsync(ClaimAdjustment adjustment, CancellationToken ct = default)
    {
        _logger.LogError(
            "ClaimAdjustment persistence is not implemented for Cosmos environments. " +
            "Configure MongoDb:ConnectionString to enable capability 5.12.");
        throw new NotImplementedException(
            "ClaimAdjustment persistence requires MongoDB. Cosmos persistence is deferred (Gap 4 ratification).");
    }

    public Task<ClaimAdjustment?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult<ClaimAdjustment?>(null);

    public Task<ClaimAdjustment?> GetByClaimVersionIdAsync(string tenantId, string claimVersionId, CancellationToken ct = default)
        => Task.FromResult<ClaimAdjustment?>(null);

    public Task<ClaimAdjustment?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default)
        => Task.FromResult<ClaimAdjustment?>(null);

    public Task<ClaimAdjustment?> GetByNewClaimIdAsync(string tenantId, string newClaimId, CancellationToken ct = default)
        => Task.FromResult<ClaimAdjustment?>(null);

    public Task<ClaimAdjustment?> GetByPredecessorAndStatusAsync(
        string tenantId,
        string predecessorClaimId,
        ClaimAdjustmentStatus status,
        CancellationToken ct = default)
        => Task.FromResult<ClaimAdjustment?>(null);

    public Task<(IReadOnlyList<ClaimAdjustment> Page, int TotalCount)> ListAsync(
        string tenantId, ClaimAdjustmentListFilter filter, CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<ClaimAdjustment>, int)>((Array.Empty<ClaimAdjustment>(), 0));

    public Task<ClaimAdjustment> UpdateAsync(ClaimAdjustment adjustment, CancellationToken ct = default)
    {
        _logger.LogError(
            "ClaimAdjustment update is not implemented for Cosmos environments. " +
            "Configure MongoDb:ConnectionString to enable capability 5.12.");
        throw new NotImplementedException(
            "ClaimAdjustment persistence requires MongoDB. Cosmos persistence is deferred (Gap 4 ratification).");
    }

    public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult(false);
}
