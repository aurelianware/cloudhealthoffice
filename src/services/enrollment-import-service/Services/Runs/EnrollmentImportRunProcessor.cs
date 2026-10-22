using CloudHealthOffice.DocumentStore;
using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services.Edi;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Services.Runs;

/// <summary>
/// Runs one raw 834 import (edi-operations.md §11):
/// <list type="number">
/// <item>Parses the stored file once without applying anything, to count its
/// units. A file that cannot be parsed fails here with nothing applied, and
/// its ISA13 is given back, as when the whole file was parsed in the request.</item>
/// <item>Parses it again and dispatches every unit, checkpointing every
/// <see cref="EnrollmentImportRunOptions.ChunkSize"/> units. A run picked up
/// after its worker stopped resumes from the last checkpoint; units already
/// begun are skipped by their unit records.</item>
/// <item>Leaves finishing to the gather (<see cref="EnrollmentRunFinisher"/>).</item>
/// </list>
/// Memory stays flat: the file is never held whole, only the units in flight.
/// </summary>
public sealed class EnrollmentImportRunProcessor(
    IEnrollmentImportRunRepository runs,
    IEnrollmentImportUnitStore units,
    IRawEdiFileStore rawFiles,
    IStreaming834Parser parser,
    IEnrollmentUnitDispatcher dispatcher,
    EnrollmentRunFinisher finisher,
    IOptions<EnrollmentImportRunOptions> options,
    TimeProvider clock,
    ILogger<EnrollmentImportRunProcessor> logger)
{
    private readonly EnrollmentImportRunOptions _options = options.Value;

    public const int MaxAttempts = 5;

    public async Task ProcessAsync(EnrollmentImportRun run, string owner, CancellationToken ct = default)
    {
        var tenantId = run.TenantId;
        if (run.Attempts > MaxAttempts)
        {
            // Something keeps stopping it (a crash on one unit, a store outage): stop retrying.
            var nothingApplied = await units.CountAsync(tenantId, run.Id, EnrollmentImportUnitStatus.Done, ct) == 0
                                 && await units.CountAsync(tenantId, run.Id, EnrollmentImportUnitStatus.Failed, ct) == 0
                                 && await units.CountAsync(tenantId, run.Id, EnrollmentImportUnitStatus.Applying, ct) == 0;
            await finisher.FailAsync(run, EnrollmentImportFailureKinds.TooManyAttempts,
                $"The import stopped {MaxAttempts} times before finishing; see the service log.", releaseControlNumbers: nothingApplied, ct);
            return;
        }
        if (run.RawDocumentKey is null)
        {
            await finisher.FailAsync(run, EnrollmentImportFailureKinds.RawFileUnavailable, "The run has no stored file.", releaseControlNumbers: true, ct);
            return;
        }

        // 1. Count the units; nothing is applied.
        if (run.ExpectedUnits is null)
        {
            int expected;
            try
            {
                expected = await CountUnitsAsync(run, ct);
            }
            catch (X12FormatException ex)
            {
                await finisher.FailAsync(run, EnrollmentImportFailureKinds.Parse, $"Could not parse 834 file: {ex.Message}", releaseControlNumbers: true, ct);
                return;
            }
            catch (Exception ex) when (IsMissingFile(ex))
            {
                logger.LogError(ex, "The stored file of import run {RunId} could not be read", LogSanitizer.Sanitize(run.Id));
                await finisher.FailAsync(run, EnrollmentImportFailureKinds.RawFileUnavailable, "The stored 834 file could not be read.", releaseControlNumbers: true, ct);
                return;
            }
            await runs.SetExpectedUnitsAsync(tenantId, run.Id, expected, ct);
            run.ExpectedUnits = expected;
            logger.LogInformation("Import run {RunId}: {Units} unit(s) to apply", LogSanitizer.Sanitize(run.Id), expected);
        }

        // 2. A previous attempt's units: any left mid-apply are failed, not applied twice.
        await units.FailInterruptedAsync(tenantId, run.Id, clock.GetUtcNow().UtcDateTime, ct);
        var checkpoint = run.DispatchedThrough;
        await CheckpointAsync(run, checkpoint, ct);

        // 3. Dispatch from the checkpoint.
        await using (var stream = await rawFiles.OpenAsync(tenantId, run.RawDocumentKey, ct))
        {
            var sinceCheckpoint = 0;
            await foreach (var parsed in parser.ParseAsync(stream, run.FileName ?? string.Empty, run.AcceptedInterchanges, ct))
            {
                if (parsed.Sequence < checkpoint) continue;
                await dispatcher.DispatchAsync(ToUnit(run, parsed), ct);
                if (++sinceCheckpoint < Math.Max(1, _options.ChunkSize)) continue;

                await dispatcher.DrainAsync(ct);
                checkpoint = parsed.Sequence + 1;
                sinceCheckpoint = 0;
                await CheckpointAsync(run, checkpoint, ct);
                if (!await runs.RenewLeaseAsync(tenantId, run.Id, owner, LeaseUntil(), ct))
                {
                    logger.LogWarning("Import run {RunId} lost its lease; another worker continues it", LogSanitizer.Sanitize(run.Id));
                    return;
                }
            }
        }
        await dispatcher.DrainAsync(ct);
        await CheckpointAsync(run, run.ExpectedUnits.Value, ct);

        // 4. The gather finishes the run once every unit is in (here, or as the last unit applies).
        await finisher.TryFinishAsync(tenantId, run.Id, ct);
    }

    public DateTime LeaseUntil() => clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(10, _options.LeaseSeconds));

    private async Task<int> CountUnitsAsync(EnrollmentImportRun run, CancellationToken ct)
    {
        await using var stream = await rawFiles.OpenAsync(run.TenantId, run.RawDocumentKey!, ct);
        var count = 0;
        await foreach (var _ in parser.ParseAsync(stream, run.FileName ?? string.Empty, run.AcceptedInterchanges, ct))
            count++;
        return count;
    }

    /// <summary>The resume point, with the unit counts taken from the unit records.</summary>
    private async Task CheckpointAsync(EnrollmentImportRun run, int dispatchedThrough, CancellationToken ct)
    {
        var completed = await units.CountAsync(run.TenantId, run.Id, EnrollmentImportUnitStatus.Done, ct);
        var failed = await units.CountAsync(run.TenantId, run.Id, EnrollmentImportUnitStatus.Failed, ct);
        await runs.SetProgressAsync(run.TenantId, run.Id, dispatchedThrough, completed, failed, ct);
    }

    private static EnrollmentUnit ToUnit(EnrollmentImportRun run, ParsedEnrollmentUnit parsed) => new()
    {
        TenantId = run.TenantId,
        RunId = run.Id,
        EdiFileId = run.EdiFileId,
        BatchId = run.BatchId,
        ActorId = run.ActorId,
        Sequence = parsed.Sequence,
        FilePosition = parsed.FilePosition,
        OrderingKey = parsed.OrderingKey,
        Kind = parsed.Kind,
        Batch = parsed.Batch,
    };

    private static bool IsMissingFile(Exception ex) =>
        ex is FileNotFoundException or DocumentNotFoundException or UnauthorizedAccessException;
}

/// <summary>Starts runs: in production by waking the worker, in tests by running them inline.</summary>
public interface IEnrollmentImportRunScheduler
{
    Task ScheduleAsync(EnrollmentImportRun run, CancellationToken ct = default);
}

/// <summary>Wakes the worker; another replica's worker finds the run by polling.</summary>
public sealed class EnrollmentImportRunSignal : IEnrollmentImportRunScheduler
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public Task ScheduleAsync(EnrollmentImportRun run, CancellationToken ct = default)
    {
        _signal.Release();
        return Task.CompletedTask;
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>Claims queued runs (and runs whose worker stopped) and processes them one at a time.</summary>
public sealed class EnrollmentImportRunWorker(
    IServiceScopeFactory scopes,
    EnrollmentImportRunSignal signal,
    IOptions<EnrollmentImportRunOptions> options,
    TimeProvider clock,
    ILogger<EnrollmentImportRunWorker> logger) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import run worker iteration failed; the run is picked up again when its lease expires");
            }

            if (!worked)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Processes the next run, if any. True when there was one.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IEnrollmentImportRunRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<EnrollmentImportRunProcessor>();
        var run = await runs.TryClaimAsync(_owner, clock.GetUtcNow().UtcDateTime, processor.LeaseUntil(), ct);
        if (run is null) return false;
        await processor.ProcessAsync(run, _owner, ct);
        return true;
    }
}
