using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.Services;

/// <summary>
/// Source of plans to evaluate on each scheduler tick. Kept as a small
/// seam so the scheduler can be unit-tested against an in-memory list
/// without standing up Mongo. Production binds
/// <see cref="MongoPlanYearScheduleSource"/>.
/// </summary>
public interface IPlanYearScheduleSource
{
    /// <summary>
    /// Yields every plan with a non-null <see cref="PlanYearDefinition"/>
    /// in any tenant. Implementations should not page — the call site is
    /// a periodic background sweep, not a hot path. Streaming via
    /// <see cref="IAsyncEnumerable{T}"/> keeps memory bounded.
    /// </summary>
    IAsyncEnumerable<BenefitPlan> EnumeratePlansAsync(CancellationToken ct);
}

/// <summary>
/// Mongo-backed source. Reads from the BenefitPlans collection and
/// filters to rows that have a PlanYearDefinition set. Cosmos is not
/// supported in Phase 1 — the events stream lives in Mongo today
/// (consistent with PlanVersionEvent).
/// </summary>
public sealed class MongoPlanYearScheduleSource : IPlanYearScheduleSource
{
    private readonly IMongoCollection<BenefitPlan> _collection;

    public MongoPlanYearScheduleSource(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "BenefitPlans";
        _collection = database.GetCollection<BenefitPlan>(collectionName);
    }

    public async IAsyncEnumerable<BenefitPlan> EnumeratePlansAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var b = Builders<BenefitPlan>.Filter;
        // Only Published versions are scheduler-relevant. Drafts have no
        // accumulator activity; Superseded versions have already been
        // replaced by a successor whose PlanYearDefinition is the source
        // of truth going forward.
        var filter = b.And(
            b.Exists(x => x.PlanYearDefinition, true),
            b.Ne(x => x.PlanYearDefinition, null!),
            b.Or(
                b.Eq(x => x.VersionState, PlanVersionState.Published),
                b.Exists(x => x.VersionState, false)));

        using var cursor = await _collection.Find(filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var plan in cursor.Current)
            {
                ct.ThrowIfCancellationRequested();
                yield return plan;
            }
        }
    }
}

/// <summary>
/// Tunables for <see cref="PlanYearScheduler"/>. Lives in
/// <c>appsettings.json</c> under <c>PlanYearScheduler:</c>.
/// </summary>
public sealed class PlanYearSchedulerOptions
{
    /// <summary>How often the scheduler scans plans. Default 6 hours.</summary>
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>
    /// How many days before <see cref="PlanYearDefinition.PlanYearEnd"/>
    /// the scheduler emits an <see cref="PlanYearTransitionType.ApproachingTransition"/>.
    /// Default 30. Set to 0 to disable approaching events.
    /// </summary>
    public int ApproachingDays { get; set; } = 30;

    /// <summary>
    /// Cosmetic startup delay so the scheduler doesn't fight other
    /// hosted services for the connection pool on cold start.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Emit-window tail for <see cref="PlanYearTransitionType.Transition"/>:
    /// once carryover has elapsed, the scheduler keeps trying to publish
    /// for this many days. Bounds how far back ancient transitions are
    /// re-checked on every sweep (the publisher would dedup them anyway,
    /// but each re-check still costs a point lookup). Default 90 days.
    /// </summary>
    public int TransitionEmitWindowDays { get; set; } = 90;
}

/// <summary>
/// Background sweep that detects approaching and just-ended plan years,
/// then emits <see cref="PlanYearTransitionEvent"/>s through
/// <see cref="IPlanYearTransitionPublisher"/>. Phase 1 ships event
/// emission only — accumulator orchestration ships in Phase 3.
///
/// <para>
/// The scheduler is safe to run on multiple replicas: idempotency lives
/// in the publisher, which keys on a deterministic
/// <see cref="PlanYearTransitionEvent.EventId"/>
/// (<c>{type}:{tenantId}:{planId}:{planYearEnd:yyyyMMdd}</c>). Two
/// replicas racing produce one row.
/// </para>
/// </summary>
public sealed class PlanYearScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly PlanYearSchedulerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlanYearScheduler> _logger;

    public PlanYearScheduler(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<PlanYearScheduler> logger)
        : this(services, BindOptions(configuration), TimeProvider.System, logger) { }

    // Test-friendly constructor: lets tests inject a fake clock and an
    // explicit options instance without touching IConfiguration.
    internal PlanYearScheduler(
        IServiceProvider services,
        PlanYearSchedulerOptions options,
        TimeProvider clock,
        ILogger<PlanYearScheduler> logger)
    {
        _services = services;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    private static PlanYearSchedulerOptions BindOptions(IConfiguration configuration)
    {
        var opts = new PlanYearSchedulerOptions();
        configuration.GetSection("PlanYearScheduler").Bind(opts);
        return opts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.StartupDelaySeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep must not take the host down. Log and try
                // again on the next tick — events are idempotent.
                _logger.LogError(ex, "PlanYearScheduler sweep failed; retrying after {Interval}", interval);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Runs a single pass over the source. Public so tests and an
    /// optional admin endpoint can trigger it on demand.
    /// </summary>
    public async Task<SweepResult> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IPlanYearScheduleSource>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPlanYearTransitionPublisher>();

        var now = _clock.GetUtcNow().UtcDateTime;
        // The scheduler only counts *attempts*. The publisher's
        // idempotency contract means a publish call can be a no-op
        // (returns an existing row); distinguishing that here would
        // require extending the publisher contract, so we name the
        // counters honestly instead.
        var approachingAttempted = 0;
        var transitionsAttempted = 0;
        var inspected = 0;

        await foreach (var plan in source.EnumeratePlansAsync(ct))
        {
            inspected++;
            if (plan.PlanYearDefinition is null) continue;

            var def = plan.PlanYearDefinition;
            var (currentStart, currentEnd) = def.ComputeWindow(now);
            var nextStart = currentEnd.AddDays(1);
            // ComputeWindow always returns the window CONTAINING now, so
            // the just-closed plan-year-end is one day before the
            // current window's start.
            var previousEnd = currentStart.AddDays(-1);

            // Transition: emit once carryover has elapsed for the
            // just-closed year. Bounded by TransitionEmitWindowDays so
            // ancient transitions don't cost a point lookup on every
            // sweep — the publisher would dedup them, but the lookup
            // still costs a roundtrip.
            var emitFrom = previousEnd.AddDays(def.CarryoverDays + 1);
            var emitUntil = emitFrom.AddDays(_options.TransitionEmitWindowDays);
            if (now.Date >= emitFrom.Date && now.Date <= emitUntil.Date)
            {
                await publisher.PublishTransitionAsync(plan, previousEnd, currentStart,
                    actorId: "scheduler", correlationId: null, ct);
                transitionsAttempted++;
            }

            // Approaching: within ApproachingDays of the CURRENT
            // window's end. Members and accumulator caches need warning
            // before the boundary, not after.
            if (_options.ApproachingDays > 0)
            {
                var approachingFrom = currentEnd.AddDays(-_options.ApproachingDays);
                if (now.Date >= approachingFrom.Date && now.Date <= currentEnd.Date)
                {
                    await publisher.PublishApproachingAsync(plan, currentEnd, nextStart,
                        actorId: "scheduler", correlationId: null, ct);
                    approachingAttempted++;
                }
            }
        }

        _logger.LogInformation(
            "PlanYearScheduler sweep complete: inspected={Inspected} approachingAttempted={ApproachingAttempted} transitionsAttempted={TransitionsAttempted}",
            inspected, approachingAttempted, transitionsAttempted);
        return new SweepResult(inspected, approachingAttempted, transitionsAttempted);
    }

    /// <summary>
    /// Counters describe scheduler-side publish *attempts*, not
    /// publisher-side inserts. The publisher dedups idempotently, so a
    /// no-op replay still increments these. Naming reflects that.
    /// </summary>
    public readonly record struct SweepResult(int Inspected, int ApproachingAttempted, int TransitionsAttempted);
}
