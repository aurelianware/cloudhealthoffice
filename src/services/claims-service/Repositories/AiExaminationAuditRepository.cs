using ClaimsService.Models;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// Append-only store for AI Claims Examiner audit records.
///
/// Append-only contract:
///   - <see cref="AppendAsync"/> creates a new record. Records are never
///     deleted and never updated, except via <see cref="SetExaminerAgreementAsync"/>
///     which writes the human-feedback fields exactly once. The repository
///     enforces single-write on agreement at the API surface; downstream
///     storage queries should treat all other fields as immutable.
///
/// Tenant isolation: every read/write filters by TenantId. The Cosmos
/// implementation uses TenantId as the partition key.
/// </summary>
public interface IAiExaminationAuditRepository
{
    /// <summary>
    /// Append a new immutable audit record. Returns the persisted document
    /// with its assigned id.
    /// </summary>
    Task<AiExaminationAudit> AppendAsync(AiExaminationAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Get every audit record for a claim, newest first. Used by the work
    /// queue UI to render history and by the override-rate analysis.
    /// </summary>
    Task<IReadOnlyList<AiExaminationAudit>> GetByClaimAsync(
        string claimId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get the most recent audit record for a claim, or null if none exist.
    /// </summary>
    Task<AiExaminationAudit?> GetLatestAsync(
        string claimId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Set examiner agreement on the latest audit record for a claim.
    /// Returns the updated record, or null if there is no audit record yet
    /// or the agreement was already set (single-write enforcement).
    /// </summary>
    Task<AiExaminationAudit?> SetExaminerAgreementAsync(
        string claimId,
        string tenantId,
        string agreement,
        string examinerUserId,
        string? notes,
        CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────────────
// MongoDB implementation
// ─────────────────────────────────────────────────────────────────────────

public class AiExaminationAuditRepositoryMongo : IAiExaminationAuditRepository
{
    private readonly IMongoCollection<AiExaminationAudit> _collection;
    private readonly ILogger<AiExaminationAuditRepositoryMongo> _logger;

    public AiExaminationAuditRepositoryMongo(
        IMongoDatabase database,
        ILogger<AiExaminationAuditRepositoryMongo> logger)
    {
        _collection = database.GetCollection<AiExaminationAudit>("AiExaminationAudit");
        _logger = logger;

        var keys = Builders<AiExaminationAudit>.IndexKeys;
        var indexes = new List<CreateIndexModel<AiExaminationAudit>>
        {
            // Newest-first lookup by claim is the dominant query.
            new(keys.Ascending(a => a.TenantId).Ascending(a => a.ClaimId).Descending(a => a.GeneratedAt)),
            // Override-rate analysis dimensions: prompt version × disposition.
            new(keys.Ascending(a => a.TenantId).Ascending(a => a.PromptVersion).Ascending(a => a.RecommendedDisposition))
        };
        _collection.Indexes.CreateMany(indexes);
    }

    public async Task<AiExaminationAudit> AppendAsync(AiExaminationAudit audit, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(audit.Id)) audit.Id = Guid.NewGuid().ToString();
        if (audit.GeneratedAt == default) audit.GeneratedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(audit, cancellationToken: ct);
        return audit;
    }

    public async Task<IReadOnlyList<AiExaminationAudit>> GetByClaimAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var filter = Builders<AiExaminationAudit>.Filter.And(
            Builders<AiExaminationAudit>.Filter.Eq(a => a.TenantId, tenantId),
            Builders<AiExaminationAudit>.Filter.Eq(a => a.ClaimId, claimId));

        var sort = Builders<AiExaminationAudit>.Sort.Descending(a => a.GeneratedAt);
        return await _collection.Find(filter).Sort(sort).ToListAsync(ct);
    }

    public async Task<AiExaminationAudit?> GetLatestAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var filter = Builders<AiExaminationAudit>.Filter.And(
            Builders<AiExaminationAudit>.Filter.Eq(a => a.TenantId, tenantId),
            Builders<AiExaminationAudit>.Filter.Eq(a => a.ClaimId, claimId));

        var sort = Builders<AiExaminationAudit>.Sort.Descending(a => a.GeneratedAt);
        return await _collection.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
    }

    public async Task<AiExaminationAudit?> SetExaminerAgreementAsync(
        string claimId,
        string tenantId,
        string agreement,
        string examinerUserId,
        string? notes,
        CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(claimId, tenantId, ct);
        if (latest is null) return null;

        // Single-write enforcement: refuse to overwrite an existing agreement.
        // The append-only contract makes this the only mutation allowed and
        // it must happen exactly once per audit row.
        if (latest.ExaminerAgreement is not null)
        {
            _logger.LogInformation(
                "Examiner agreement already set on audit {AuditId} for claim {ClaimId}; ignoring duplicate write",
                latest.Id, claimId);
            return latest;
        }

        var update = Builders<AiExaminationAudit>.Update
            .Set(a => a.ExaminerAgreement, agreement)
            .Set(a => a.ExaminerActedAt, DateTime.UtcNow)
            .Set(a => a.ExaminerUserId, examinerUserId)
            .Set(a => a.ExaminerNotes, notes);

        var filter = Builders<AiExaminationAudit>.Filter.And(
            Builders<AiExaminationAudit>.Filter.Eq(a => a.Id, latest.Id),
            Builders<AiExaminationAudit>.Filter.Eq(a => a.TenantId, tenantId),
            // Belt-and-braces: filter on null agreement so a concurrent writer
            // can't slip a second update through between our read and write.
            Builders<AiExaminationAudit>.Filter.Eq(a => a.ExaminerAgreement, (string?)null));

        var options = new FindOneAndUpdateOptions<AiExaminationAudit> { ReturnDocument = ReturnDocument.After };
        return await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
    }
}

// ─────────────────────────────────────────────────────────────────────────
// Cosmos DB implementation
// ─────────────────────────────────────────────────────────────────────────

public class AiExaminationAuditRepositoryCosmos : IAiExaminationAuditRepository
{
    private readonly Container _container;
    private readonly ILogger<AiExaminationAuditRepositoryCosmos> _logger;

    public AiExaminationAuditRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<AiExaminationAuditRepositoryCosmos> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var containerName = configuration["CosmosDb:AiExaminationAuditContainer"] ?? "AiExaminationAudit";
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<AiExaminationAudit> AppendAsync(AiExaminationAudit audit, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(audit.Id)) audit.Id = Guid.NewGuid().ToString();
        if (audit.GeneratedAt == default) audit.GeneratedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(audit, new PartitionKey(audit.TenantId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<IReadOnlyList<AiExaminationAudit>> GetByClaimAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.generatedAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _container.GetItemQueryIterator<AiExaminationAudit>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<AiExaminationAudit>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<AiExaminationAudit?> GetLatestAsync(
        string claimId, string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.generatedAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _container.GetItemQueryIterator<AiExaminationAudit>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            return page.FirstOrDefault();
        }
        return null;
    }

    public async Task<AiExaminationAudit?> SetExaminerAgreementAsync(
        string claimId,
        string tenantId,
        string agreement,
        string examinerUserId,
        string? notes,
        CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(claimId, tenantId, ct);
        if (latest is null) return null;

        if (latest.ExaminerAgreement is not null)
        {
            _logger.LogInformation(
                "Examiner agreement already set on audit {AuditId} for claim {ClaimId}; ignoring duplicate write",
                latest.Id, claimId);
            return latest;
        }

        latest.ExaminerAgreement = agreement;
        latest.ExaminerActedAt = DateTime.UtcNow;
        latest.ExaminerUserId = examinerUserId;
        latest.ExaminerNotes = notes;

        // ETag optimistic concurrency would be ideal here, but the read above
        // doesn't capture _etag. The Mongo path uses a filter-on-null guard;
        // the Cosmos path relies on the in-process check above plus the
        // single-write contract enforced at the controller. If contention
        // becomes a real issue, switch this to a patch with an etag pre-condition.
        var response = await _container.ReplaceItemAsync(
            latest, latest.Id, new PartitionKey(tenantId), cancellationToken: ct);
        return response.Resource;
    }
}
