namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>
/// Process-local exclusion store, used when no MongoDB is configured
/// (development, single-replica hosts) and in tests. Each replica downloads
/// and holds its own copy.
/// </summary>
public sealed class InMemoryExclusionRecordStore : IExclusionRecordStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ExclusionScreeningSource, List<ExclusionRecord>> _records = new();
    private readonly Dictionary<ExclusionScreeningSource, ExclusionSyncStatus> _status = new();

    public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<ExclusionDatasetLoadResult> ReplaceDatasetAsync(
        ExclusionScreeningSource source,
        IEnumerable<ExclusionRecord> records,
        ExclusionDatasetLoadPolicy policy,
        CancellationToken ct = default)
    {
        var syncId = Guid.NewGuid().ToString("N");
        var loaded = new List<ExclusionRecord>();
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            record.Source = source;
            record.SyncId = syncId;
            loaded.Add(record);
        }

        lock (_gate)
        {
            var previous = _status.TryGetValue(source, out var s) ? s.RecordCount : 0;
            var rejection = ExclusionDatasetLoadChecks.Reject(loaded.Count, previous, policy);
            if (rejection is not null)
            {
                return Task.FromResult(new ExclusionDatasetLoadResult
                {
                    Succeeded = false, RecordCount = loaded.Count, PreviousRecordCount = previous, Error = rejection
                });
            }

            _records[source] = loaded;
            var status = GetOrCreate(source);
            status.ActiveSyncId = syncId;
            status.LastSuccessfulSyncAt = policy.SyncedAt;
            status.LastAttemptAt = policy.SyncedAt;
            status.RecordCount = loaded.Count;
            status.SourceUrl = policy.SourceUrl;
            status.LastError = null;

            return Task.FromResult(new ExclusionDatasetLoadResult
            {
                Succeeded = true, RecordCount = loaded.Count, PreviousRecordCount = previous, SyncId = syncId
            });
        }
    }

    public Task<IReadOnlyList<ExclusionRecord>> FindByNpiAsync(ExclusionScreeningSource source, string syncId, string npi, CancellationToken ct = default) =>
        Find(source, r => r.SyncId == syncId && r.Npi == npi);

    public Task<IReadOnlyList<ExclusionRecord>> FindByLastNameAsync(ExclusionScreeningSource source, string syncId, string normalizedLastName, CancellationToken ct = default) =>
        Find(source, r => r.SyncId == syncId && r.NormalizedLastName == normalizedLastName);

    public Task<IReadOnlyList<ExclusionRecord>> FindByBusinessNameAsync(ExclusionScreeningSource source, string syncId, string normalizedBusinessName, CancellationToken ct = default) =>
        Find(source, r => r.SyncId == syncId && r.NormalizedBusinessName == normalizedBusinessName);

    public Task<ExclusionSyncStatus?> GetSyncStatusAsync(ExclusionScreeningSource source, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_status.TryGetValue(source, out var s) ? Clone(s) : null);
        }
    }

    public Task RecordSyncFailureAsync(ExclusionScreeningSource source, string error, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var status = GetOrCreate(source);
            status.LastAttemptAt = at;
            status.LastError = error;
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireSyncLeaseAsync(ExclusionScreeningSource source, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var status = GetOrCreate(source);
            if (status.LeaseUntil is { } until && until > now && status.LeaseHolder != holder)
                return Task.FromResult(false);
            status.LeaseHolder = holder;
            status.LeaseUntil = now + duration;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseSyncLeaseAsync(ExclusionScreeningSource source, string holder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_status.TryGetValue(source, out var status) && status.LeaseHolder == holder)
            {
                status.LeaseHolder = null;
                status.LeaseUntil = null;
            }
        }
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<ExclusionRecord>> Find(ExclusionScreeningSource source, Func<ExclusionRecord, bool> predicate)
    {
        lock (_gate)
        {
            IReadOnlyList<ExclusionRecord> found = _records.TryGetValue(source, out var list)
                ? list.Where(predicate).ToList()
                : [];
            return Task.FromResult(found);
        }
    }

    private ExclusionSyncStatus GetOrCreate(ExclusionScreeningSource source)
    {
        if (!_status.TryGetValue(source, out var status))
        {
            status = new ExclusionSyncStatus { Source = source };
            _status[source] = status;
        }
        return status;
    }

    private static ExclusionSyncStatus Clone(ExclusionSyncStatus s) => new()
    {
        Source = s.Source,
        ActiveSyncId = s.ActiveSyncId,
        LastSuccessfulSyncAt = s.LastSuccessfulSyncAt,
        RecordCount = s.RecordCount,
        SourceUrl = s.SourceUrl,
        LastAttemptAt = s.LastAttemptAt,
        LastError = s.LastError,
        LeaseHolder = s.LeaseHolder,
        LeaseUntil = s.LeaseUntil
    };
}
