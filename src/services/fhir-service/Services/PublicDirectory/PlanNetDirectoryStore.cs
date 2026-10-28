using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// Holds the current snapshot of each published directory.
///
/// The first request for a directory builds its snapshot and waits for it.
/// After that, a request finding the snapshot older than
/// <see cref="PublicProviderDirectoryOptions.RefreshMinutes"/> starts one
/// rebuild in the background and is answered from the snapshot it has; the
/// new snapshot replaces the old one only once it is complete. A failed
/// rebuild keeps the old snapshot. After a failure, no build is tried again
/// for <see cref="RetryAfterFailure"/>, so an unavailable upstream is
/// not hit by every public request.
///
/// Builds run outside the request that triggered them (no caller context
/// flows into them), so they authenticate as fhir-service, for the
/// directory's tenant, and are not cancelled when that caller disconnects.
/// </summary>
public sealed class PlanNetDirectoryStore
{
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IPlanNetDirectorySource _source;
    private readonly IOptionsMonitor<PublicProviderDirectoryOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanNetDirectoryStore> _logger;

    public PlanNetDirectoryStore(
        IPlanNetDirectorySource source,
        IOptionsMonitor<PublicProviderDirectoryOptions> options,
        TimeProvider time,
        ILogger<PlanNetDirectoryStore> logger)
    {
        _source = source;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The snapshot of <paramref name="directoryId"/>. Throws
    /// <see cref="PlanNetDirectoryUnavailableException"/> when there is none
    /// and one cannot be built now.
    /// </summary>
    public async Task<PlanNetDirectorySnapshot> GetAsync(string directoryId, string tenantId, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd($"{directoryId}\n{tenantId}", _ => new Entry(directoryId, tenantId));

        var current = entry.Snapshot;
        if (current is not null)
        {
            var maxAge = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.RefreshMinutes));
            if (_time.GetUtcNow() - entry.BuiltAt > maxAge && !RecentlyFailed(entry))
                _ = StartBuild(entry);
            return current;
        }

        if (RecentlyFailed(entry))
            throw new PlanNetDirectoryUnavailableException(entry.FailureMessage ?? "The directory is unavailable.");

        var built = await StartBuild(entry).WaitAsync(ct);
        return built ?? throw new PlanNetDirectoryUnavailableException(entry.FailureMessage ?? "The directory is unavailable.");
    }

    private bool RecentlyFailed(Entry entry)
        => entry.FailedAt is { } failedAt && _time.GetUtcNow() - failedAt < RetryAfterFailure;

    private Task<PlanNetDirectorySnapshot?> StartBuild(Entry entry)
    {
        lock (entry)
        {
            if (entry.Building is { IsCompleted: false } running) return running;
            using (ExecutionContext.SuppressFlow())
            {
                entry.Building = Task.Run(() => BuildAsync(entry));
            }
            return entry.Building;
        }
    }

    private async Task<PlanNetDirectorySnapshot?> BuildAsync(Entry entry)
    {
        try
        {
            var snapshot = await _source.BuildAsync(entry.DirectoryId, entry.TenantId, CancellationToken.None);
            entry.BuiltAt = _time.GetUtcNow();
            entry.Snapshot = snapshot;
            entry.FailedAt = null;
            entry.FailureMessage = null;
            return snapshot;
        }
        catch (Exception ex)
        {
            entry.FailedAt = _time.GetUtcNow();
            entry.FailureMessage = ex is PlanNetDirectoryUnavailableException ? ex.Message : "The directory could not be built.";
            _logger.LogWarning(ex,
                "Public directory {Directory} could not be built{Stale}",
                entry.DirectoryId, entry.Snapshot is null ? string.Empty : "; serving the previous snapshot");
            return null;
        }
    }

    private sealed class Entry(string directoryId, string tenantId)
    {
        public string DirectoryId { get; } = directoryId;
        public string TenantId { get; } = tenantId;
        public volatile PlanNetDirectorySnapshot? Snapshot;
        public DateTimeOffset BuiltAt;
        public Task<PlanNetDirectorySnapshot?>? Building;
        public DateTimeOffset? FailedAt;
        public string? FailureMessage;
    }
}
