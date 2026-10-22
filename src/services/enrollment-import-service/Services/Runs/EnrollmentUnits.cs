using System.Threading.Channels;
using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services.Edi;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Services.Runs;

/// <summary>Settings for raw 834 import runs (section <c>EnrollmentImport:Runs</c>).</summary>
public sealed class EnrollmentImportRunOptions
{
    public const string SectionName = "EnrollmentImport:Runs";

    /// <summary>In-process lanes units are applied on, partitioned by tenant and subscriber id.</summary>
    public int Lanes { get; set; } = 1;

    /// <summary>Units dispatched between checkpoints (the resume point and the lease renewal).</summary>
    public int ChunkSize { get; set; } = 500;

    public int LeaseSeconds { get; set; } = 120;

    /// <summary>How often an idle worker looks for runs another replica queued.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>How long the upload endpoint waits for its run before answering 202.</summary>
    public double WaitSeconds { get; set; } = 10;

    /// <summary>Largest upload accepted.</summary>
    public long MaxUploadBytes { get; set; } = 1L << 30;
}

/// <summary>
/// One unit of work of an import run (edi-operations.md §11): a subscriber
/// with its dependents, or one standalone dependent, with everything needed
/// to apply it anywhere. The future Kafka topic <c>enrollment.units.v1</c>
/// carries this, keyed by tenant + <see cref="OrderingKey"/>.
/// </summary>
public sealed class EnrollmentUnit
{
    public required string TenantId { get; init; }
    public required string RunId { get; init; }
    public string? EdiFileId { get; init; }
    public required string BatchId { get; init; }
    public string? ActorId { get; init; }
    public required int Sequence { get; init; }
    public required string FilePosition { get; init; }

    /// <summary>The subscriber id: units with the same tenant and key apply in file order.</summary>
    public required string OrderingKey { get; init; }

    public required EnrollmentUnitKind Kind { get; init; }
    public required Enrollment834 Batch { get; init; }

    /// <summary>The partition key: tenant + subscriber id.</summary>
    public string PartitionKey => $"{TenantId}\u001f{OrderingKey}";
}

/// <summary>
/// Hands units to whatever applies them. This PR applies them in process; a
/// later one publishes them to <c>enrollment.units.v1</c> and lets consumers
/// apply them. Either way units with the same <see cref="EnrollmentUnit.PartitionKey"/>
/// apply in the order dispatched, and the run is finished by the gather
/// (<see cref="EnrollmentRunFinisher"/>), not by the dispatcher.
/// </summary>
public interface IEnrollmentUnitDispatcher
{
    ValueTask DispatchAsync(EnrollmentUnit unit, CancellationToken ct = default);

    /// <summary>
    /// Returns once every unit dispatched so far has been handed off for good:
    /// applied, for the in-process dispatcher; published, for a broker.
    /// </summary>
    Task DrainAsync(CancellationToken ct = default);
}

/// <summary>Applies one unit. Idempotent by run and sequence; never throws.</summary>
public interface IEnrollmentUnitApplier
{
    Task ApplyAsync(EnrollmentUnit unit, CancellationToken ct = default);
}

/// <summary>
/// N in-process lanes (<see cref="EnrollmentImportRunOptions.Lanes"/>, default
/// 1), each applying its units in order. A unit's lane is a stable hash of its
/// partition key, so a subscriber's units never run concurrently or out of order.
/// </summary>
public sealed class InProcessEnrollmentUnitDispatcher : IEnrollmentUnitDispatcher
{
    private readonly IEnrollmentUnitApplier _applier;
    private readonly ILogger<InProcessEnrollmentUnitDispatcher> _logger;
    private readonly Channel<EnrollmentUnit>[] _lanes;
    private int _pending;

    public InProcessEnrollmentUnitDispatcher(IEnrollmentUnitApplier applier, IOptions<EnrollmentImportRunOptions> options,
        ILogger<InProcessEnrollmentUnitDispatcher> logger)
    {
        _applier = applier;
        _logger = logger;
        var lanes = Math.Clamp(options.Value.Lanes, 1, 64);
        _lanes = new Channel<EnrollmentUnit>[lanes];
        for (var i = 0; i < lanes; i++)
        {
            // Bounded: dispatch waits when a lane is behind, so parsing never runs far ahead of applying.
            var lane = Channel.CreateBounded<EnrollmentUnit>(new BoundedChannelOptions(256) { SingleReader = true });
            _lanes[i] = lane;
            _ = Task.Run(() => RunLaneAsync(lane.Reader));
        }
    }

    public int LaneCount => _lanes.Length;

    /// <summary>FNV-1a over the partition key: stable across processes, unlike string.GetHashCode.</summary>
    public static int LaneFor(string partitionKey, int lanes)
    {
        var hash = 2166136261u;
        foreach (var c in partitionKey)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash % (uint)lanes);
    }

    public async ValueTask DispatchAsync(EnrollmentUnit unit, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pending);
        try
        {
            await _lanes[LaneFor(unit.PartitionKey, _lanes.Length)].Writer.WriteAsync(unit, ct);
        }
        catch
        {
            Interlocked.Decrement(ref _pending);
            throw;
        }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        while (Volatile.Read(ref _pending) > 0)
            await Task.Delay(5, ct);
    }

    private async Task RunLaneAsync(ChannelReader<EnrollmentUnit> reader)
    {
        await foreach (var unit in reader.ReadAllAsync())
        {
            try
            {
                await _applier.ApplyAsync(unit);
            }
            catch (Exception ex)
            {
                // The applier records its own failures; this is a bug guard so the lane keeps running.
                _logger.LogError(ex, "Unit {Sequence} of import run {RunId} could not be applied", unit.Sequence, LogSanitizer.Sanitize(unit.RunId));
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }
}

/// <summary>
/// Applies a unit through the same import as a whole file
/// (<see cref="IEnrollmentImportService.ImportEnrollmentAsync"/>), once:
/// the unit record is written before it is applied, so a redelivery is
/// skipped. Then it adds the outcome to the run and offers the run to the gather.
/// </summary>
public sealed class EnrollmentUnitApplier(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<EnrollmentUnitApplier> logger) : IEnrollmentUnitApplier
{
    public async Task ApplyAsync(EnrollmentUnit unit, CancellationToken ct = default)
    {
        try
        {
            // One scope per unit, as one request: the import and the EDI stores are scoped.
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var units = services.GetRequiredService<IEnrollmentImportUnitStore>();
            var runs = services.GetRequiredService<IEnrollmentImportRunRepository>();
            var finisher = services.GetRequiredService<EnrollmentRunFinisher>();
            var exceptions = services.GetService<Repositories.IEnrollmentExceptionRepository>();
            if (!await units.TryBeginAsync(unit.TenantId, unit.RunId, unit.Sequence, unit.FilePosition, clock.GetUtcNow().UtcDateTime, ct))
            {
                logger.LogDebug("Unit {Sequence} of import run {RunId} was already applied; skipped", unit.Sequence, LogSanitizer.Sanitize(unit.RunId));
                return;
            }

            ImportResult result;
            string? error = null;
            string? exceptionId = null;
            var batch = unit.Batch;
            // The change as received, for an exception should the whole unit fail.
            var received = EnrollmentUnitExceptions.Copy(batch);
            batch.BatchId = unit.BatchId;
            batch.ActorId = unit.ActorId;
            batch.EdiFileId = unit.EdiFileId;
            batch.RecordRun = false;
            try
            {
                var import = services.GetRequiredService<IEnrollmentImportService>();
                result = await import.ImportEnrollmentAsync(batch, unit.TenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unit {Sequence} ({Position}) of import run {RunId} failed", unit.Sequence,
                    LogSanitizer.Sanitize(unit.FilePosition), LogSanitizer.Sanitize(unit.RunId));
                // The file position and a fixed text: the exception's own text (which can
                // carry member data or hosts) stays in the log, as ClientErrorText.
                error = $"{unit.FilePosition}: {EnrollmentImportUnitStore.FailedError}";
                result = new ImportResult { Errors = [error] };
                exceptionId = await OpenExceptionAsync(exceptions, unit, received,
                    EnrollmentRejectionReasons.UnexpectedError, EnrollmentImportUnitStore.FailedError, ct);
            }

            var failed = error is not null;
            await units.FinishAsync(unit.TenantId, unit.RunId, unit.Sequence, failed, error, clock.GetUtcNow().UtcDateTime, exceptionId, ct);
            await runs.AddUnitResultAsync(unit.TenantId, unit.RunId, result, failed, ct);
            await finisher.TryFinishAsync(unit.TenantId, unit.RunId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The unit record stays Applying; the run's next attempt marks it failed and opens its exception.
            logger.LogError(ex, "Could not record unit {Sequence} of import run {RunId}", unit.Sequence, LogSanitizer.Sanitize(unit.RunId));
        }
    }

    /// <summary>Opens the unit's exception (once: its id is deterministic). Null when it could not be stored.</summary>
    internal async Task<string?> OpenExceptionAsync(Repositories.IEnrollmentExceptionRepository? exceptions, EnrollmentUnit unit,
        Enrollment834 received, string reasonCode, string message, CancellationToken ct)
    {
        if (exceptions is null) return null;
        var exception = EnrollmentUnitExceptions.Build(unit, received, reasonCode, message, clock.GetUtcNow().UtcDateTime);
        try
        {
            await exceptions.TryCreateAsync(exception, ct);
            return exception.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not open the enrollment exception for unit {Sequence} of import run {RunId}",
                unit.Sequence, LogSanitizer.Sanitize(unit.RunId));
            return null;
        }
    }
}
