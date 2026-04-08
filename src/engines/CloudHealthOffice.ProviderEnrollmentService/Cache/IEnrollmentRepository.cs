using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Enrollment record cache — persists StateEnrollmentRecords fetched from
/// live state APIs so subsequent lookups within the TTL window avoid
/// redundant external API calls.
///
/// Partition strategy:
///   Cosmos: /stateCode  (query patterns are always state-scoped)
///   Mongo:  { npi: 1, stateCode: 1 }  compound index
/// </summary>
public interface IEnrollmentRepository
{
    /// <summary>
    /// Fetch the cached enrollment record for a given NPI and state.
    /// Returns null on a cache miss — callers must then invoke the live source.
    /// </summary>
    Task<StateEnrollmentRecord?> GetAsync(
        string npi,
        string stateCode,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch all cached records for a provider across all states.
    /// Used by the aggregator to short-circuit live calls when all states are cached.
    /// </summary>
    Task<IReadOnlyList<StateEnrollmentRecord>> GetAllStatesAsync(
        string npi,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or replace a cached enrollment record.
    /// CachedAt is always set to UtcNow by the repository implementation.
    /// </summary>
    Task UpsertAsync(StateEnrollmentRecord record, CancellationToken ct = default);

    /// <summary>
    /// Bulk upsert for batch sync workers — more efficient than individual UpsertAsync calls.
    /// </summary>
    Task BulkUpsertAsync(
        IEnumerable<StateEnrollmentRecord> records,
        CancellationToken ct = default);

    /// <summary>
    /// Return all providers whose revalidation date falls within the look-ahead window.
    /// Used by RevalidationAlertEngine — scoped to a single state when stateCode is provided.
    /// </summary>
    Task<IReadOnlyList<StateEnrollmentRecord>> GetProvidersWithRevalidationDueSoonAsync(
        int withinDays,
        string? stateCode = null,
        CancellationToken ct = default);

    /// <summary>
    /// Return all NPIs with an Active record for a given state and MCO participant.
    /// Used by McoPanelReconciliationService to pull the enrolled panel for a plan.
    /// </summary>
    Task<IReadOnlyList<StateEnrollmentRecord>> GetActivePanelByMcoAsync(
        string stateCode,
        string mcoId,
        CancellationToken ct = default);

    /// <summary>Delete the cached record for a given NPI + state (e.g., after termination).</summary>
    Task DeleteAsync(string npi, string stateCode, CancellationToken ct = default);
}
