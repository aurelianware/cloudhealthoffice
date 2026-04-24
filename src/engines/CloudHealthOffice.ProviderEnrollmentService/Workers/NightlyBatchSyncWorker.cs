using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Workers;

/// <summary>
/// Nightly batch sync worker — pulls fresh enrollment data from all state sources.
///
/// Execution model:
///   Runs as a KEDA-triggered Kubernetes Job (not a long-running BackgroundService).
///   The KEDA HTTP Add-on wakes the pod; the worker runs, exits with code 0 on success.
///
///   KEDA CronJob schedule (helm values):
///     triggers:
///       - type: cron
///         metadata:
///           timezone: "America/Chicago"
///           start: "0 2 * * *"     # 2:00 AM CT daily
///           end:   "0 4 * * *"     # hard stop at 4:00 AM CT
///
/// State sync order:
///   1. Texas PEMS (SFTP batch export — highest priority for Texas MCO tenants)
///   2. Other state sources in registration order
///   3. CAQH panel refresh (API fan-out, not a true bulk sync)
/// </summary>
public sealed class NightlyBatchSyncWorker : IHostedService
{
    private readonly IEnumerable<IStateEnrollmentSource> _sources;
    private readonly ProviderEnrollmentOptions _opts;
    private readonly ILogger<NightlyBatchSyncWorker> _logger;

    public NightlyBatchSyncWorker(
        IEnumerable<IStateEnrollmentSource> sources,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<NightlyBatchSyncWorker> logger)
    {
        _sources = sources;
        _opts    = options.Value;
        _logger  = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("NightlyBatchSyncWorker started at {Time} UTC", DateTime.UtcNow);

        var results = new List<BatchSyncResult>();

        // Prioritize TX PEMS — always run first regardless of registration order
        var txSource = _sources.FirstOrDefault(s => s.StateCode == "TX");
        if (txSource is not null)
        {
            _logger.LogInformation("Syncing TX PEMS (priority source)");
            var result = await RunSyncWithTimeoutAsync(txSource, TimeSpan.FromMinutes(60), ct);
            results.Add(result);
            LogSyncResult(result);
        }

        // Remaining sources in parallel — each with a 30-minute timeout
        var remainingSources = _sources
            .Where(s => s.StateCode != "TX")
            .Where(s => _opts.EnabledStateCodes.Count == 0 ||
                        _opts.EnabledStateCodes.Contains(s.StateCode, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var remainingTasks = remainingSources.Select(source =>
            RunSyncWithTimeoutAsync(source, TimeSpan.FromMinutes(30), ct));

        var remainingResults = await Task.WhenAll(remainingTasks);
        foreach (var result in remainingResults)
        {
            results.Add(result);
            LogSyncResult(result);
        }

        // Summary
        var totalProcessed = results.Sum(r => r.RecordsProcessed);
        var totalUpserted  = results.Sum(r => r.RecordsUpserted);
        var totalErrors    = results.Sum(r => r.Errors);

        _logger.LogInformation(
            "NightlyBatchSyncWorker complete: {Sources} sources, {Processed} records, " +
            "{Upserted} upserted, {Errors} errors",
            results.Count, totalProcessed, totalUpserted, totalErrors);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("NightlyBatchSyncWorker stopping");
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<BatchSyncResult> RunSyncWithTimeoutAsync(
        IStateEnrollmentSource source,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await source.BulkSyncAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Sync for {State}/{System} timed out after {Timeout}",
                source.StateCode, source.SourceSystemName, timeout);

            return new BatchSyncResult
            {
                StateCode        = source.StateCode,
                SourceSystem     = source.SourceSystemName,
                SyncStarted      = DateTime.UtcNow,
                SyncCompleted    = DateTime.UtcNow,
                Errors           = 1,
                ErrorDetails     = [$"Sync timed out after {timeout.TotalMinutes:0} minutes"]
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Sync for {State}/{System} failed with unhandled exception",
                source.StateCode, source.SourceSystemName);

            return new BatchSyncResult
            {
                StateCode        = source.StateCode,
                SourceSystem     = source.SourceSystemName,
                SyncStarted      = DateTime.UtcNow,
                SyncCompleted    = DateTime.UtcNow,
                Errors           = 1,
                ErrorDetails     = [ex.Message]
            };
        }
    }

    private void LogSyncResult(BatchSyncResult r)
    {
        if (r.Errors > 0)
            _logger.LogWarning(
                "{State}/{System}: {Processed} processed, {Upserted} upserted, {Errors} errors: {ErrorDetails}",
                r.StateCode, r.SourceSystem, r.RecordsProcessed, r.RecordsUpserted,
                r.Errors, string.Join("; ", r.ErrorDetails.Take(3)));
        else
            _logger.LogInformation(
                "{State}/{System}: {Processed} processed, {Upserted} upserted",
                r.StateCode, r.SourceSystem, r.RecordsProcessed, r.RecordsUpserted);
    }
}
