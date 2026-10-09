using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Observability;

namespace AppealsService.HostedServices;

/// <summary>Relay for the appeal transactional outbox.</summary>
public interface IAppealOutboxDispatcher
{
    /// <summary>
    /// Low-latency nudge right after a change: starts publishing the appeal's
    /// outbox in the background and returns immediately (a Kafka timeout
    /// never adds to request latency). Never throws: whatever is not
    /// published here stays pending for the sweep. No-op when
    /// <see cref="AppealOutboxOptions.DispatchInline"/> is off or the relay
    /// is paused by a Kafka outage.
    /// </summary>
    Task NotifyChangedAsync(string tenantId, string appealId, CancellationToken ct = default);

    /// <summary>Process one appeal: publish its due pending entries in order, then housekeeping. Returns how many were published.</summary>
    Task<int> DispatchAppealAsync(string tenantId, string appealId, CancellationToken ct = default);

    /// <summary>One sweep over appeals with due work. Returns how many entries were published.</summary>
    Task<int> DispatchPendingAsync(CancellationToken ct = default);
}

/// <summary>
/// Publishes pending <see cref="AppealOutboxMessage"/>s to Kafka and marks
/// them sent. Delivery is at-least-once: an event is marked sent only after
/// the broker acknowledged it, so a crash between the produce and the mark
/// republishes it with the same <c>eventId</c> and <c>sequence</c>.
///
/// Per-appeal order: entries are published in outbox (write) order; an
/// entry is attempted only after every earlier pending entry of the same
/// appeal was acknowledged or dead-lettered. Each entry gets the appeal's
/// next <see cref="AppealOutboxMessage.Sequence"/> right before its first
/// attempt, so sequences follow write order and consumers can apply
/// last-write-wins by sequence.
///
/// Lease: every entry renews the per-appeal lease first, and every outcome
/// write is conditioned on still holding it, on the entry's server id and
/// on the entry still being pending. When renewal fails (the lease lapsed
/// and another dispatcher took over) the loop stops, so a stale holder
/// cannot overwrite the new holder's results.
///
/// Failures:
/// <list type="bullet">
///   <item>Transient (see <see cref="IAppealEventTransport.IsTransient"/>:
///     outage, missing topic, revoked ACL, SASL failure, fatal producer):
///     the entry stays pending without using an attempt and the relay
///     pauses with exponential backoff; it never dead-letters.</item>
///   <item>Non-transient (the message itself is rejected): attempts grow
///     with exponential backoff; after <see cref="AppealOutboxOptions.MaxAttempts"/>
///     the entry is dead-lettered (error log +
///     <c>cho.appeals.outbox.outcomes.total{cho.outcome=dead_lettered}</c>)
///     and later entries proceed.</item>
/// </list>
///
/// Housekeeping (every visit, and every <see cref="AppealOutboxOptions.MaintenanceInterval"/>
/// while publishing is impossible): pending entries older than
/// <see cref="AppealOutboxOptions.MaxPendingAge"/> or beyond
/// <see cref="AppealOutboxOptions.MaxPendingPerAppeal"/> are dead-lettered
/// (<c>expired</c> / <c>overflow</c>); sent / skipped entries are pruned after
/// <see cref="AppealOutboxOptions.SentRetention"/> or beyond
/// <see cref="AppealOutboxOptions.MaxCompletedPerAppeal"/>; dead letters
/// after <see cref="AppealOutboxOptions.DeadLetterRetention"/> or beyond
/// <see cref="AppealOutboxOptions.MaxDeadLetteredPerAppeal"/>. That bounds
/// every appeal document's outbox.
/// </summary>
public sealed class AppealOutboxDispatcher : BackgroundService, IAppealOutboxDispatcher
{
    private const int LockStripes = 64;

    private readonly IServiceScopeFactory? _scopes;
    private readonly IAppealOutboxStore? _store;
    private readonly IAppealEventTransport _transport;
    private readonly AppealOutboxOptions _options;
    private readonly ILogger<AppealOutboxDispatcher> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _pauseSync = new();
    private DateTime _pausedUntil = DateTime.MinValue;
    private int _consecutiveTransientFailures;

    /// <summary>Lease owner id for this process.</summary>
    internal string InstanceId { get; init; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public AppealOutboxDispatcher(
        IServiceScopeFactory scopes,
        IAppealEventTransport transport,
        AppealOutboxOptions options,
        ILogger<AppealOutboxDispatcher> logger,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _transport = transport;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Test constructor over a fixed store.</summary>
    internal AppealOutboxDispatcher(
        IAppealOutboxStore store,
        IAppealEventTransport transport,
        AppealOutboxOptions options,
        ILogger<AppealOutboxDispatcher> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _transport = transport;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private enum Mode { Publish, Skip, Retain }

    private static Mode ModeFor(AppealEventPublisherState state, AppealOutboxOptions options) => state switch
    {
        AppealEventPublisherState.Available => Mode.Publish,
        AppealEventPublisherState.Disabled when options.SkipWhenKafkaDisabled => Mode.Skip,
        _ => Mode.Retain
    };

    private async Task<T> WithStoreAsync<T>(Func<IAppealOutboxStore, Task<T>> work)
    {
        if (_store is not null) return await work(_store);
        using var scope = _scopes!.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IAppealOutboxStore>());
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!_options.Enabled)
        {
            _logger.LogWarning("AppealOutbox:Enabled=false — the background outbox relay is off.");
            return;
        }

        AppealEventPublisherState state;
        try
        {
            state = await _transport.Started.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var mode = ModeFor(state, _options);
        switch (state)
        {
            case AppealEventPublisherState.Disabled when mode == Mode.Retain:
                _logger.LogWarning(
                    "Kafka publishing is disabled by configuration: appeal events stay pending in the outbox " +
                    "(dead-lettered after {MaxAge}) and are published once Kafka is configured.", _options.MaxPendingAge);
                break;
            case AppealEventPublisherState.Unavailable:
                _logger.LogError(
                    "Kafka producer failed to start: appeal events stay pending in the outbox until a restart succeeds.");
                break;
        }

        var nextStats = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Now >= nextStats)
                {
                    await RefreshStatsAsync(stoppingToken);
                    nextStats = Now + _options.StatsInterval;
                }
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Appeal outbox sweep failed; retrying after the poll interval.");
            }

            var delay = mode == Mode.Retain ? _options.MaintenanceInterval : _options.PollInterval;
            if (mode == Mode.Retain && _options.StatsInterval < delay) delay = _options.StatsInterval;
            var pause = PausedFor();
            if (pause > delay) delay = pause;
            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Refresh the backlog gauges.</summary>
    internal async Task RefreshStatsAsync(CancellationToken ct = default)
    {
        var stats = await WithStoreAsync(s => s.GetStatsAsync(ct));
        var age = stats.OldestPendingCreatedAt is { } oldest ? Math.Max(0, (Now - oldest).TotalSeconds) : 0;
        ChoMetrics.SetAppealOutboxBacklog(stats.Pending, age);
    }

    public async Task NotifyChangedAsync(string tenantId, string appealId, CancellationToken ct = default)
    {
        if (!_options.DispatchInline) return;
        if (PausedFor() > TimeSpan.Zero) return; // Kafka outage: the sweep resumes after the pause

        if (_options.AwaitInlineDispatch)
        {
            await DispatchInlineAsync(tenantId, appealId, ct);
            return;
        }

        // Fire-and-forget on the relay's own lifetime, not the request's.
        _ = Task.Run(() => DispatchInlineAsync(tenantId, appealId, _shutdown.Token), CancellationToken.None);
    }

    private async Task DispatchInlineAsync(string tenantId, string appealId, CancellationToken ct)
    {
        try
        {
            await DispatchAppealAsync(tenantId, appealId, ct);
        }
        catch (Exception ex)
        {
            // The change and its event are committed; the sweep publishes it.
            _logger.LogWarning(ex,
                "Inline outbox dispatch failed for appeal {AppealId}; the background relay will publish it.",
                LogSanitizer.SafeForLog(appealId));
        }
    }

    public async Task<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var mode = await CurrentModeAsync();
        if (mode is null) return 0;
        if (mode == Mode.Publish && PausedFor() > TimeSpan.Zero) return 0;

        var keys = await WithStoreAsync(s => s.FindDueAsync(Now, _options.BatchSize, ct));
        var published = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (mode == Mode.Publish && PausedFor() > TimeSpan.Zero) break;
            try
            {
                published += await DispatchAppealAsync(key.TenantId, key.AppealId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch failed for appeal {AppealId}; retrying on the next sweep.",
                    LogSanitizer.SafeForLog(key.AppealId));
            }
        }
        return published;
    }

    private async Task<Mode?> CurrentModeAsync()
    {
        var started = _transport.Started;
        if (!started.IsCompleted) return null;
        return ModeFor(await started, _options);
    }

    public async Task<int> DispatchAppealAsync(string tenantId, string appealId, CancellationToken ct = default)
    {
        if (await CurrentModeAsync() is not { } mode) return 0;
        // Paused by an outage: do not even take the lease.
        if (mode == Mode.Publish && PausedFor() > TimeSpan.Zero) return 0;

        var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode($"{tenantId}:{appealId}") % LockStripes)];
        await gate.WaitAsync(ct);
        try
        {
            return await WithStoreAsync(store => DispatchLeasedAsync(store, mode, tenantId, appealId, ct));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> DispatchLeasedAsync(
        IAppealOutboxStore store, Mode mode, string tenantId, string appealId, CancellationToken ct)
    {
        var now = Now;
        var lease = await store.TryLeaseAsync(tenantId, appealId, InstanceId, now, now + _options.LeaseDuration, ct);
        if (lease is null) return 0;

        var entries = lease.Entries;
        var sequence = lease.LastSequence;
        var published = 0;
        try
        {
            if (!await ExpireAsync(store, tenantId, appealId, entries)) return 0;
            if (mode == Mode.Retain) return 0; // housekeeping only

            foreach (var entry in entries)
            {
                if (entry.Status != AppealOutboxStatus.Pending) continue;
                now = Now;

                if (mode == Mode.Skip)
                {
                    entry.Status = AppealOutboxStatus.Skipped;
                    entry.CompletedAt = now;
                    if (!await SaveAsync(store, tenantId, appealId, entry)) break;
                    Record("skipped", entry);
                    continue;
                }

                // Order: a later entry never overtakes one still waiting.
                if (entry.NextAttemptAt is { } due && due > now) break;
                if (PausedFor() > TimeSpan.Zero) break;

                // Renew the lease (and take this entry's sequence on its
                // first attempt); losing it means another dispatcher owns
                // the appeal now — stop.
                var assign = entry.Sequence is null
                    ? new AppealOutboxSequenceAssignment(entry.Id, sequence + 1)
                    : null;
                if (!await store.RenewLeaseAsync(tenantId, appealId, InstanceId, now + _options.LeaseDuration, assign,
                        CancellationToken.None))
                {
                    _logger.LogWarning("Outbox lease on appeal {AppealId} was lost; stopping this run.",
                        LogSanitizer.SafeForLog(appealId));
                    break;
                }
                if (assign is not null)
                {
                    entry.Sequence = assign.Sequence;
                    sequence = assign.Sequence;
                }

                try
                {
                    await _transport.ProduceAsync(entry, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!await RecordFailureAsync(store, tenantId, appealId, entry, ex)) break;
                    continue;
                }

                ResetPause();
                entry.Status = AppealOutboxStatus.Sent;
                entry.CompletedAt = Now;
                entry.LastAttemptAt = entry.CompletedAt;
                entry.NextAttemptAt = null;
                entry.LastError = null;
                published++;
                Record("published", entry);
                // Not cancellable: the broker has the event; record that.
                if (!await SaveAsync(store, tenantId, appealId, entry)) break;
            }
        }
        finally
        {
            try
            {
                await store.ReleaseLeaseAsync(tenantId, appealId, InstanceId, PruneIds(entries, Now), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Releasing the outbox lease for appeal {AppealId} failed; it lapses on its own.",
                    LogSanitizer.SafeForLog(appealId));
            }
        }
        return published;
    }

    /// <summary>Conditional outcome write; false (lease lost / entry no longer pending) stops the run.</summary>
    private async Task<bool> SaveAsync(IAppealOutboxStore store, string tenantId, string appealId, AppealOutboxMessage entry)
    {
        if (await store.UpdateMessageAsync(tenantId, appealId, InstanceId, entry, CancellationToken.None)) return true;
        _logger.LogWarning(
            "Outbox entry {EventId} of appeal {AppealId} was not updated: the lease moved or the entry is no longer pending. Stopping this run.",
            LogSanitizer.SafeForLog(entry.EventId), LogSanitizer.SafeForLog(appealId));
        return false;
    }

    /// <summary>Dead-letter pending entries that are too old or beyond the per-appeal cap. False = lease lost.</summary>
    private async Task<bool> ExpireAsync(
        IAppealOutboxStore store, string tenantId, string appealId, IReadOnlyList<AppealOutboxMessage> entries)
    {
        var now = Now;
        var pending = entries.Where(m => m.Status == AppealOutboxStatus.Pending).ToList();
        var overflow = pending.Take(Math.Max(0, pending.Count - _options.MaxPendingPerAppeal)).ToHashSet();
        foreach (var entry in pending)
        {
            string? reason = null;
            if (now - entry.CreatedAt > _options.MaxPendingAge) reason = "expired";
            else if (overflow.Contains(entry)) reason = "overflow";
            if (reason is null) continue;

            entry.Status = AppealOutboxStatus.DeadLettered;
            entry.CompletedAt = now;
            entry.ExpiresAt = now + _options.DeadLetterRetention;
            entry.NextAttemptAt = null;
            entry.LastError = reason == "expired"
                ? $"Expired: pending longer than AppealOutbox:MaxPendingAge ({_options.MaxPendingAge})."
                : $"Overflow: more than AppealOutbox:MaxPendingPerAppeal ({_options.MaxPendingPerAppeal}) pending events.";
            if (!await SaveAsync(store, tenantId, appealId, entry)) return false;
            Record(reason, entry);
            _logger.LogError(
                "Appeal outbox event dead-lettered ({Reason}): {EventType} {EventId} appeal {AppealId} tenant {TenantId}. " +
                "Replay with POST /api/appeals/{{id}}/outbox/replay before {Expires}.",
                reason, LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
                LogSanitizer.SafeForLog(appealId), LogSanitizer.SafeForLog(tenantId), entry.ExpiresAt);
        }
        return true;
    }

    /// <summary>Completed entries past retention or beyond the per-appeal caps (oldest first).</summary>
    private List<string> PruneIds(IReadOnlyList<AppealOutboxMessage> entries, DateTime now)
    {
        var prune = new List<string>();

        var completed = entries.Where(m => m.Status is AppealOutboxStatus.Sent or AppealOutboxStatus.Skipped).ToList();
        var keepCompleted = completed.Skip(Math.Max(0, completed.Count - _options.MaxCompletedPerAppeal)).ToHashSet();
        prune.AddRange(completed
            .Where(m => !keepCompleted.Contains(m) || m.CompletedAt < now - _options.SentRetention)
            .Select(m => m.Id));

        var dead = entries.Where(m => m.Status == AppealOutboxStatus.DeadLettered).ToList();
        var keepDead = dead.Skip(Math.Max(0, dead.Count - _options.MaxDeadLetteredPerAppeal)).ToHashSet();
        foreach (var m in dead.Where(m => !keepDead.Contains(m) || (m.ExpiresAt is { } e && e <= now)))
        {
            prune.Add(m.Id);
            Record("dead_letter_pruned", m);
            _logger.LogWarning(
                "Pruning dead-lettered appeal event {EventType} {EventId} of appeal {AppealId} (retention or cap reached); " +
                "it is no longer replayable from the outbox. The audit trail keeps the change.",
                LogSanitizer.SafeForLog(m.EventType), LogSanitizer.SafeForLog(m.EventId), LogSanitizer.SafeForLog(m.AppealId));
        }
        return prune;
    }

    /// <summary>Returns true when the next entry of the appeal may proceed (this one was dead-lettered).</summary>
    private async Task<bool> RecordFailureAsync(
        IAppealOutboxStore store, string tenantId, string appealId, AppealOutboxMessage entry, Exception ex)
    {
        var now = Now;
        entry.LastAttemptAt = now;
        entry.LastError = LogSanitizer.SafeForLog($"{ex.GetType().Name}: {ex.Message}", 500);

        if (_transport.IsTransient(ex))
        {
            var pause = Pause();
            await SaveAsync(store, tenantId, appealId, entry);
            Record("failed", entry, transient: true);
            _logger.LogWarning(
                "Kafka unavailable publishing {EventType} {EventId} for appeal {AppealId}; kept pending, relay paused for {Pause}. {Error}",
                LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
                LogSanitizer.SafeForLog(appealId), pause, entry.LastError);
            return false;
        }

        entry.Attempts++;
        if (entry.Attempts >= _options.MaxAttempts)
        {
            entry.Status = AppealOutboxStatus.DeadLettered;
            entry.CompletedAt = now;
            entry.ExpiresAt = now + _options.DeadLetterRetention;
            entry.NextAttemptAt = null;
            if (!await SaveAsync(store, tenantId, appealId, entry)) return false;
            Record("dead_lettered", entry);
            _logger.LogError(
                "Appeal outbox event dead-lettered after {Attempts} attempts: {EventType} {EventId} appeal {AppealId} tenant {TenantId}. " +
                "Later events of this appeal continue; replay with POST /api/appeals/{{id}}/outbox/replay. Last error: {Error}",
                entry.Attempts, LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
                LogSanitizer.SafeForLog(appealId), LogSanitizer.SafeForLog(tenantId), entry.LastError);
            return true;
        }

        entry.NextAttemptAt = now + _options.BackoffFor(entry.Attempts);
        await SaveAsync(store, tenantId, appealId, entry);
        Record("failed", entry);
        _logger.LogWarning(
            "Publishing {EventType} {EventId} for appeal {AppealId} failed (attempt {Attempts}/{Max}); retry at {Next}. {Error}",
            LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
            LogSanitizer.SafeForLog(appealId), entry.Attempts, _options.MaxAttempts, entry.NextAttemptAt, entry.LastError);
        return false;
    }

    internal TimeSpan PausedFor()
    {
        lock (_pauseSync)
        {
            var left = _pausedUntil - Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    private TimeSpan Pause()
    {
        lock (_pauseSync)
        {
            _consecutiveTransientFailures++;
            var pause = _options.BackoffFor(_consecutiveTransientFailures);
            _pausedUntil = Now + pause;
            return pause;
        }
    }

    private void ResetPause()
    {
        lock (_pauseSync)
        {
            _consecutiveTransientFailures = 0;
            _pausedUntil = DateTime.MinValue;
        }
    }

    private static void Record(string outcome, AppealOutboxMessage entry, bool transient = false) =>
        ChoMetrics.AppealOutboxOutcomes.Add(1,
            new KeyValuePair<string, object?>("cho.outcome", outcome),
            new KeyValuePair<string, object?>("cho.event_type", entry.EventType),
            new KeyValuePair<string, object?>("cho.transient", transient));
}
