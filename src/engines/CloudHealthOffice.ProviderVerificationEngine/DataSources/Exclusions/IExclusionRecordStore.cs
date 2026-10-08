namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>
/// Tenant-agnostic local copy of the federal exclusion lists (LEIE, SAM
/// extract) plus per-source sync bookkeeping.
/// </summary>
public interface IExclusionRecordStore
{
    Task EnsureIndexesAsync(CancellationToken ct = default);

    /// <summary>
    /// Load a complete new copy of <paramref name="source"/>'s list. Rows are
    /// written under a new sync id; only when the full file loaded and passed
    /// the size checks is the sync status switched to it and the previous
    /// copy deleted. On any failure the new rows are removed and the previous
    /// dataset (and its sync timestamp) stays in place.
    /// </summary>
    Task<ExclusionDatasetLoadResult> ReplaceDatasetAsync(
        ExclusionScreeningSource source,
        IEnumerable<ExclusionRecord> records,
        ExclusionDatasetLoadPolicy policy,
        CancellationToken ct = default);

    // Lookups are scoped to one committed snapshot: pass
    // ExclusionSyncStatus.ActiveSyncId. Rows of an in-flight, rejected or
    // superseded load are never visible to screening.

    Task<IReadOnlyList<ExclusionRecord>> FindByNpiAsync(ExclusionScreeningSource source, string syncId, string npi, CancellationToken ct = default);

    Task<IReadOnlyList<ExclusionRecord>> FindByLastNameAsync(ExclusionScreeningSource source, string syncId, string normalizedLastName, CancellationToken ct = default);

    Task<IReadOnlyList<ExclusionRecord>> FindByBusinessNameAsync(ExclusionScreeningSource source, string syncId, string normalizedBusinessName, CancellationToken ct = default);

    Task<ExclusionSyncStatus?> GetSyncStatusAsync(ExclusionScreeningSource source, CancellationToken ct = default);

    Task RecordSyncFailureAsync(ExclusionScreeningSource source, string error, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Take the per-source sync lease so only one replica downloads at a time.</summary>
    Task<bool> TryAcquireSyncLeaseAsync(ExclusionScreeningSource source, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct = default);

    Task ReleaseSyncLeaseAsync(ExclusionScreeningSource source, string holder, CancellationToken ct = default);
}

public sealed class ExclusionDatasetLoadPolicy
{
    public required string SourceUrl { get; init; }
    public required DateTimeOffset SyncedAt { get; init; }
    public int MinimumRecordCount { get; init; }

    /// <summary>Reject a load smaller than this fraction of the previous record count.</summary>
    public double MinimumRetainedFraction { get; init; }
}

public sealed class ExclusionDatasetLoadResult
{
    public bool Succeeded { get; init; }
    public int RecordCount { get; init; }
    public int PreviousRecordCount { get; init; }
    public string? SyncId { get; init; }
    public string? Error { get; init; }
}

internal static class ExclusionDatasetLoadChecks
{
    /// <summary>Null when the load may be committed; otherwise why it must be rejected.</summary>
    public static string? Reject(int count, int previousCount, ExclusionDatasetLoadPolicy policy)
    {
        if (count < policy.MinimumRecordCount)
            return $"Loaded {count} records, below the minimum of {policy.MinimumRecordCount}; keeping previous dataset";
        if (previousCount > 0 && count < previousCount * policy.MinimumRetainedFraction)
            return $"Loaded {count} records versus {previousCount} previously (below {policy.MinimumRetainedFraction:P0}); keeping previous dataset";
        return null;
    }
}
