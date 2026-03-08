using CloudHealthOffice.BenefitEngine.Domain;

namespace CloudHealthOffice.BenefitEngine.Persistence;

/// <summary>
/// Low-level storage interface for accumulator documents.
///
/// The ChoAccumulatorService operates at the logical level
/// (individual + family documents, idempotency, retries).
/// This interface handles the raw document reads and writes.
///
/// Two implementations are provided:
///   AccumulatorRepositoryMongo  — MongoDB with version-stamp optimistic concurrency
///   AccumulatorRepositoryCosmos — Cosmos DB with ETag optimistic concurrency
/// </summary>
public interface IAccumulatorRepository
{
    /// <summary>
    /// Load an accumulator document for a specific owner and scope.
    /// Returns null if no document exists yet (first-time member encounter).
    /// </summary>
    Task<AccumulatorDocument?> GetAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or replace the document using optimistic concurrency.
    ///
    /// For new documents (Version == 0): InsertOne / CreateItem.
    /// For existing documents: ReplaceOne with version filter / ReplaceItem with ETag.
    ///
    /// Returns the saved document (with updated Version/ETag).
    /// Throws <see cref="OptimisticConcurrencyException"/> if a concurrent
    /// write has modified the document since it was last read.
    /// </summary>
    Task<AccumulatorDocument> UpsertAsync(
        AccumulatorDocument document,
        CancellationToken ct = default);

    /// <summary>
    /// Delete all accumulator documents for a plan year (annual reset job).
    /// Deletes both individual and family documents for the given plan.
    /// </summary>
    Task DeleteByPlanYearAsync(
        string tenantId, Guid benefitPlanId, string planYear,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a concurrent write has modified an accumulator document
/// since the caller last read it. The caller should reload and retry.
/// </summary>
public sealed class OptimisticConcurrencyException : Exception
{
    public OptimisticConcurrencyException(string documentId)
        : base($"Optimistic concurrency conflict on accumulator document '{documentId}'. " +
               "The document was modified by a concurrent claim. Reload and retry.")
    {
        DocumentId = documentId;
    }

    /// <summary>The Id of the document that had a conflict.</summary>
    public string DocumentId { get; }
}
