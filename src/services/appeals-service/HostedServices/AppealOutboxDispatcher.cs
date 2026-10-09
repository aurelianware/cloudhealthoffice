using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Observability;

namespace AppealsService.HostedServices;

/// <summary>Relay for the appeal transactional outbox.</summary>
public interface IAppealOutboxDispatcher
{
    /// <summary>
    /// Best-effort, low-latency publish of an appeal's outbox right after a
    /// change. Never throws: whatever is not published here stays pending
    /// for the background sweep. No-op when
    /// <see cref="AppealOutboxOptions.DispatchInline"/> is off.
    /// </summary>
    Task NotifyChangedAsync(string tenantId, string appealId, CancellationToken ct = default);

    /// <summary>Publish one appeal's due pending entries, in order. Returns how many were published.</summary>
    Task<int> DispatchAppealAsync(string tenantId, string appealId, CancellationToken ct = default);

    /// <summary>One sweep over appeals with pending entries. Returns how many entries were published.</summary>
    Task<int> DispatchPendingAsync(CancellationToken ct = default);
}

/// <summary>
/// Publishes pending <see cref="AppealOutboxMessage"/>s to Kafka and marks
/// them sent. Delivery is at-least-once: an event is marked sent only after
/// the broker acknowledged it, so a crash between the produce and the mark
/// republishes it with the same <c>eventId</c> (consumers de-duplicate on
/// it, also carried as the <c>event-id</c> header).
///
/// Per-appeal order: entries are published in outbox (write) order, and an
/// entry is attempted only after every earlier pending entry of the same
/// appeal was acknowledged. A failed entry blocks the ones after it until
/// it is sent or dead-lettered. Because any dispatcher publishes in order
/// and only advances after an ack, the first delivery of each event keeps
/// write order even if two dispatchers overlap after a lease lapse.
///
/// Failures:
/// <list type="bullet">
///   <item>Transient (Kafka unreachable, timeouts — see
///     <see cref="IAppealEventTransport.IsTransient"/>): the entry stays
///     pending and does not use up an attempt; the whole dispatcher pauses
///     with exponential backoff, so an outage never dead-letters events.</item>
///   <item>Non-transient (the message itself is rejected): the entry's
///     <see cref="AppealOutboxMessage.Attempts"/> grows and it retries with
///     exponential backoff; after <see cref="AppealOutboxOptions.MaxAttempts"/>
///     it becomes <see cref="AppealOutboxStatus.DeadLettered"/> (error log +
///     <c>cho.appeals.outbox.outcomes.total{cho.outcome=dead_lettered}</c>),
///     and later entries of the appeal proceed.</item>
/// </list>
///
/// Kafka disabled by configuration: entries stay pending (default) or are
/// marked <see cref="AppealOutboxStatus.Skipped"/> when
/// <see cref="AppealOutboxOptions.SkipWhenKafkaDisabled"/> is set. Kafka
/// configured but the producer failed to build: entries stay pending.
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
    private readonly object _pauseSync = new();
    private DateTime _pausedUntil = DateTime.MinValue;
    private int _consecutiveTransientFailures;

    /// <summary>Lease owner id for this process.</summary>
    internal string InstanceId { get; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

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

    private async Task<T> WithStoreAsync<T>(Func<IAppealOutboxStore, Task<T>> work)
    {
        if (_store is not null) return await work(_store);
        using var scope = _scopes!.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IAppealOutboxStore>());
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

        switch (state)
        {
            case AppealEventPublisherState.Disabled when !_options.SkipWhenKafkaDisabled:
                _logger.LogWarning(
                    "Kafka publishing is disabled by configuration: appeal events stay pending in the outbox " +
                    "and are published once Kafka is configured.");
                return;
            case AppealEventPublisherState.Unavailable:
                _logger.LogError(
                    "Kafka producer failed to start: appeal events stay pending in the outbox until a restart succeeds.");
                return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

            var delay = _options.PollInterval;
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

    public async Task NotifyChangedAsync(string tenantId, string appealId, CancellationToken ct = default)
    {
        if (!_options.DispatchInline) return;
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
        if (PausedFor() > TimeSpan.Zero) return 0;
        var keys = await WithStoreAsync(s => s.FindPendingAsync(Now, _options.BatchSize, ct));
        var published = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (PausedFor() > TimeSpan.Zero) break;
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

    public async Task<int> DispatchAppealAsync(string tenantId, string appealId, CancellationToken ct = default)
    {
        var started = _transport.Started;
        if (!started.IsCompleted) return 0;
        var state = await started;
        if (state == AppealEventPublisherState.Unavailable) return 0;
        if (state == AppealEventPublisherState.Disabled && !_options.SkipWhenKafkaDisabled) return 0;

        var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode($"{tenantId}:{appealId}") % LockStripes)];
        await gate.WaitAsync(ct);
        try
        {
            return await WithStoreAsync(store => DispatchLeasedAsync(store, state, tenantId, appealId, ct));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> DispatchLeasedAsync(
        IAppealOutboxStore store, AppealEventPublisherState state, string tenantId, string appealId, CancellationToken ct)
    {
        var now = Now;
        var entries = await store.TryLeaseAsync(tenantId, appealId, InstanceId, now, now + _options.LeaseDuration, ct);
        if (entries is null) return 0;

        var published = 0;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Status != AppealOutboxStatus.Pending) continue;
                now = Now;

                if (state == AppealEventPublisherState.Disabled)
                {
                    entry.Status = AppealOutboxStatus.Skipped;
                    entry.CompletedAt = now;
                    await store.UpdateMessageAsync(tenantId, appealId, entry, CancellationToken.None);
                    Record("skipped", entry);
                    continue;
                }

                // Order: a later entry never overtakes one still waiting.
                if (entry.NextAttemptAt is { } due && due > now) break;
                if (PausedFor() > TimeSpan.Zero) break;

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
                // Not cancellable: the broker has the event; record that.
                await store.UpdateMessageAsync(tenantId, appealId, entry, CancellationToken.None);
                Record("published", entry);
                published++;
            }
        }
        finally
        {
            try
            {
                await store.ReleaseLeaseAsync(tenantId, appealId, InstanceId, Now - _options.SentRetention, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Releasing the outbox lease for appeal {AppealId} failed; it lapses on its own.",
                    LogSanitizer.SafeForLog(appealId));
            }
        }
        return published;
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
            await store.UpdateMessageAsync(tenantId, appealId, entry, CancellationToken.None);
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
            entry.NextAttemptAt = null;
            await store.UpdateMessageAsync(tenantId, appealId, entry, CancellationToken.None);
            Record("dead_lettered", entry);
            _logger.LogError(
                "Appeal outbox event dead-lettered after {Attempts} attempts: {EventType} {EventId} appeal {AppealId} tenant {TenantId}. " +
                "Later events of this appeal continue; replay with POST /api/appeals/{{id}}/outbox/replay. Last error: {Error}",
                entry.Attempts, LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
                LogSanitizer.SafeForLog(appealId), LogSanitizer.SafeForLog(tenantId), entry.LastError);
            return true;
        }

        entry.NextAttemptAt = now + _options.BackoffFor(entry.Attempts);
        await store.UpdateMessageAsync(tenantId, appealId, entry, CancellationToken.None);
        Record("failed", entry);
        _logger.LogWarning(
            "Publishing {EventType} {EventId} for appeal {AppealId} failed (attempt {Attempts}/{Max}); retry at {Next}. {Error}",
            LogSanitizer.SafeForLog(entry.EventType), LogSanitizer.SafeForLog(entry.EventId),
            LogSanitizer.SafeForLog(appealId), entry.Attempts, _options.MaxAttempts, entry.NextAttemptAt, entry.LastError);
        return false;
    }

    private TimeSpan PausedFor()
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
