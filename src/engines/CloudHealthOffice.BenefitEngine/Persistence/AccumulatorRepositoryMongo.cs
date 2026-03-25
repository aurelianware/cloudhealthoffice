using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.BenefitEngine.Persistence;

/// <summary>
/// MongoDB-backed accumulator repository.
///
/// Optimistic concurrency: every replace filters on both document Id AND the
/// expected Version value. If another claim updated the document between our
/// read and our write, ModifiedCount == 0 and we throw
/// OptimisticConcurrencyException so the caller can reload and retry.
///
/// Recommended index (create once at tenant onboarding):
///   db.Accumulators.createIndex(
///     { tenantId: 1, benefitPlanId: 1, planYear: 1 },
///     { name: "idx_accumulators_tenant_plan_year" }
///   )
///
/// The document _id is the composite key string so the default _id index
/// covers single-document lookups without an additional index.
/// </summary>
public class AccumulatorRepositoryMongo : IAccumulatorRepository
{
    private readonly IMongoCollection<AccumulatorDocument> _collection;
    private readonly ILogger<AccumulatorRepositoryMongo> _logger;

    public AccumulatorRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<AccumulatorRepositoryMongo> logger)
    {
        var collectionName = configuration["BenefitEngine:AccumulatorCollection"] ?? "Accumulators";
        _collection = database.GetCollection<AccumulatorDocument>(collectionName);
        _logger = logger;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public async Task<AccumulatorDocument?> GetAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var id = AccumulatorDocument.MakeId(tenantId, scope.ToString(), ownerId, benefitPlanId, planYear);

        var filter = Builders<AccumulatorDocument>.Filter.And(
            Builders<AccumulatorDocument>.Filter.Eq(x => x.Id, id),
            Builders<AccumulatorDocument>.Filter.Eq(x => x.TenantId, tenantId));

        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<AccumulatorDocument> UpsertAsync(
        AccumulatorDocument document,
        CancellationToken ct = default)
    {
        var expectedVersion = document.Version;
        document.Version = expectedVersion + 1;
        document.LastUpdated = DateTime.UtcNow;

        if (expectedVersion == 0)
        {
            // New document — insert. Duplicate key = concurrent insert won the race.
            try
            {
                await _collection.InsertOneAsync(document, cancellationToken: ct);
                _logger.LogDebug("Inserted new accumulator document {DocId}", SanitizeForLog(document.Id));
                return document;
            }
            catch (MongoWriteException ex)
                when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new OptimisticConcurrencyException(document.Id);
            }
        }

        // Existing document — replace with version guard.
        // If another write incremented the version first, ModifiedCount == 0.
        var filter = Builders<AccumulatorDocument>.Filter.And(
            Builders<AccumulatorDocument>.Filter.Eq(x => x.Id, document.Id),
            Builders<AccumulatorDocument>.Filter.Eq(x => x.TenantId, document.TenantId),
            Builders<AccumulatorDocument>.Filter.Eq(x => x.Version, expectedVersion));

        var result = await _collection.ReplaceOneAsync(filter, document, cancellationToken: ct);

        if (result.ModifiedCount == 0)
        {
            _logger.LogDebug(
                "Optimistic concurrency conflict on {DocId} (expected version {Version})",
                SanitizeForLog(document.Id), expectedVersion);
            throw new OptimisticConcurrencyException(document.Id);
        }

        return document;
    }

    public async Task DeleteByPlanYearAsync(
        string tenantId, Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorDocument>.Filter.And(
            Builders<AccumulatorDocument>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<AccumulatorDocument>.Filter.Eq(x => x.BenefitPlanId, benefitPlanId),
            Builders<AccumulatorDocument>.Filter.Eq(x => x.PlanYear, planYear));

        var result = await _collection.DeleteManyAsync(filter, ct);
        _logger.LogInformation(
            "Deleted {Count} accumulator documents for plan {PlanId} / year {PlanYear}",
            result.DeletedCount, benefitPlanId, planYear);
    }
}
