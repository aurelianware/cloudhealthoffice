namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Keeps the local exclusion datasets (LEIE file, SAM extract) current.
/// Every <see cref="ExclusionScreeningOptions.SyncCheckInterval"/> it syncs
/// any dataset whose last successful sync is older than
/// <see cref="ExclusionScreeningOptions.SyncInterval"/> (after a failure it
/// waits <see cref="ExclusionScreeningOptions.SyncRetryDelay"/>). A per-source
/// lease in the store keeps replicas from downloading concurrently. Failures
/// are logged and never stop the host; screening keeps using the previous
/// dataset until it ages past the staleness window, after which providers are
/// reported NOT screened.
/// </summary>
public sealed class ExclusionDatasetSyncHostedService : BackgroundService
{
    private readonly IReadOnlyList<IExclusionDatasetSync> _syncs;
    private readonly IExclusionRecordStore _store;
    private readonly ExclusionScreeningOptions _options;
    private readonly ILogger<ExclusionDatasetSyncHostedService> _logger;
    private readonly TimeProvider _time;

    public ExclusionDatasetSyncHostedService(
        IEnumerable<IExclusionDatasetSync> syncs,
        IExclusionRecordStore store,
        IOptions<ExclusionScreeningOptions> options,
        ILogger<ExclusionDatasetSyncHostedService> logger,
        TimeProvider? time = null)
    {
        _syncs = syncs.ToList();
        _store = store;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_syncs.Count == 0)
            return;

        try
        {
            await _store.EnsureIndexesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Creating exclusion store indexes failed; continuing");
        }

        await RunDueSyncsAsync(stoppingToken).ConfigureAwait(false);

        var interval = _options.SyncCheckInterval > TimeSpan.Zero ? _options.SyncCheckInterval : TimeSpan.FromHours(1);
        using var timer = new PeriodicTimer(interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunDueSyncsAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Run every sync that is due now. Public for tests and admin triggers.</summary>
    public async Task RunDueSyncsAsync(CancellationToken ct)
    {
        foreach (var sync in _syncs)
        {
            try
            {
                if (!await IsDueAsync(sync, ct).ConfigureAwait(false))
                    continue;

                var result = await sync.SyncAsync(ct).ConfigureAwait(false);
                if (!result.Succeeded && result.SkippedReason is null)
                {
                    _logger.LogWarning("Scheduled {Source} exclusion sync did not succeed: {Error}",
                        LocalExclusionListScreener.Label(sync.Source), result.Error);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled {Source} exclusion sync failed unexpectedly",
                    LocalExclusionListScreener.Label(sync.Source));
            }
        }
    }

    private async Task<bool> IsDueAsync(IExclusionDatasetSync sync, CancellationToken ct)
    {
        var status = await _store.GetSyncStatusAsync(sync.Source, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (status?.LastSuccessfulSyncAt is { } last && now - last < _options.SyncInterval)
            return false;

        // Back off after a failed attempt instead of hammering the publisher
        // (SAM keys can be limited to a handful of requests per day).
        if (status?.LastAttemptAt is { } attempt && status.LastError is not null && now - attempt < _options.SyncRetryDelay)
            return false;

        return true;
    }
}
