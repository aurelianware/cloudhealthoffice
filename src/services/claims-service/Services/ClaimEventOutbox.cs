using System.Diagnostics.Metrics;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Events.Kafka;
using CloudHealthOffice.Infrastructure.Observability;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services;

/// <summary>Settings for the claim-event outbox (<c>Claims:EventOutbox</c>).</summary>
public sealed class ClaimEventOutboxOptions
{
    public const string SectionName = "Claims:EventOutbox";

    /// <summary>Run the background dispatcher (the request path always publishes once at once).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; set; } = 100;

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A pending event older than this is logged at error on every poll (alert on it).</summary>
    public TimeSpan AgeAlert { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>Metrics of the claim-event outbox (meter <see cref="ChoMetrics.MeterName"/>).</summary>
public static class ClaimEventOutboxMetrics
{
    private static readonly Meter Meter = new(ChoMetrics.MeterName);
    private static long _oldestAgeSeconds;

    /// <summary>Publish attempts by <c>result</c> (published / rescheduled).</summary>
    public static readonly Counter<long> Attempts = Meter.CreateCounter<long>(
        "cho.claims.event_outbox.attempts.total", unit: "{attempt}",
        description: "Claim-event outbox publish attempts by result");

    /// <summary>Age of the oldest pending claim event (alert when it grows).</summary>
    public static readonly ObservableGauge<long> OldestAge = Meter.CreateObservableGauge(
        "cho.claims.event_outbox.oldest_age", () => Interlocked.Read(ref _oldestAgeSeconds), unit: "s",
        description: "Age of the oldest pending claim event");

    public static void SetOldestAge(TimeSpan? age) =>
        Interlocked.Exchange(ref _oldestAgeSeconds, age is { } a ? (long)a.TotalSeconds : 0);
}

/// <summary>
/// Publishes a claim's pending events (<see cref="Claim.PendingClaimEvents"/>)
/// oldest first and removes each one Kafka acknowledges. The first event that
/// is not acknowledged is rescheduled with backoff and the rest wait behind
/// it, so a claim's events (Paid, then Reversed) never reach consumers out of
/// order. Never throws on a publish failure: the event stays in the outbox.
/// Delivery is at least once (a crash after the acknowledgement and before the
/// removal publishes it again); consumers deduplicate on the event id.
/// </summary>
public sealed class ClaimEventOutboxDrainer
{
    private readonly IChoEventProducer _producer;
    private readonly ClaimEventOutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaimEventOutboxDrainer> _logger;

    public ClaimEventOutboxDrainer(
        IChoEventProducer producer, IOptions<ClaimEventOutboxOptions> options,
        ILogger<ClaimEventOutboxDrainer> logger, TimeProvider? clock = null)
    {
        _producer = producer;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when Kafka is configured: events are queued and published.</summary>
    public bool IsEnabled => _producer.IsConfigured;

    /// <param name="respectDueTimes">
    /// The dispatcher leaves an event that is still in its backoff (and every
    /// later one) alone; the request path that just wrote the claim publishes at once.
    /// </param>
    /// <returns>How many events were published.</returns>
    public async Task<int> DrainAsync(IClaimRepository claims, Claim claim, bool respectDueTimes, CancellationToken ct)
    {
        if (claim.PendingClaimEvents is not { Count: > 0 } pending) return 0;

        var published = 0;
        var nowMs = AccumulatorOutboxItem.ToMs(_clock.GetUtcNow().UtcDateTime);
        foreach (var item in pending.OrderBy(e => e.CreatedAtMs).ToList())
        {
            if (respectDueTimes && item.DueAtMs > nowMs) break;
            try
            {
                await _producer.ProduceAsync(item.Topic, item.Key, item.Payload, item.Headers, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await RescheduleAsync(claims, claim, item, ex);
                break;
            }

            await claims.CompleteClaimEventAsync(claim.TenantId, claim.Id, item.Id, CancellationToken.None);
            ClaimEventOutboxMetrics.Attempts.Add(1, new KeyValuePair<string, object?>("result", "published"));
            _logger.LogInformation("Published claim event {EventId} for claim {ClaimId} to {Topic}",
                LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id), item.Topic);
            published++;
        }
        return published;
    }

    private async Task RescheduleAsync(IClaimRepository claims, Claim claim, ClaimEventOutboxItem item, Exception ex)
    {
        var attempts = item.Attempts + 1;
        var factor = Math.Pow(2, Math.Min(attempts - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(_options.BaseBackoff.Ticks * factor, _options.MaxBackoff.Ticks));
        var due = AccumulatorOutboxItem.ToMs(_clock.GetUtcNow().UtcDateTime + delay);
        // Error text: exception type and broker error code only (no claim data).
        var error = ex is KafkaException kafka ? $"{ex.GetType().Name} {kafka.Error.Code}" : ex.GetType().Name;
        try
        {
            await claims.RescheduleClaimEventAsync(claim.TenantId, claim.Id, item.Id, attempts, due, error, CancellationToken.None);
        }
        catch (Exception rescheduleEx)
        {
            // The event is still pending (with its old due time); the next poll retries it.
            _logger.LogError(rescheduleEx, "Could not reschedule claim event {EventId} for claim {ClaimId}",
                LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id));
        }
        ClaimEventOutboxMetrics.Attempts.Add(1, new KeyValuePair<string, object?>("result", "rescheduled"));
        _logger.LogWarning(ex,
            "Claim event {EventId} for claim {ClaimId} not acknowledged by Kafka (attempt {Attempts}); kept in the outbox, next attempt in {Delay}",
            LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id), attempts, delay);
    }
}

/// <summary>
/// Background dispatcher of the claim-event outbox: every
/// <see cref="ClaimEventOutboxOptions.PollInterval"/> it drains the claims of
/// every tenant with a due pending event, updates the oldest-age gauge, and
/// logs an error while an event is older than <see cref="ClaimEventOutboxOptions.AgeAlert"/>.
/// Several replicas may run it: a claim drained twice at once publishes an
/// event twice, which consumers tolerate (they deduplicate on the event id).
/// It scans only the base claims database, so it refuses to start over
/// tenant-scoped databases (<c>MongoDb:UseTenantScoping</c>), where it would
/// never see a tenant's pending events.
/// </summary>
public sealed class ClaimEventOutboxDispatcher : BackgroundService
{
    public const string TenantScopingSetting = "MongoDb:UseTenantScoping";

    private readonly IServiceScopeFactory _scopes;
    private readonly ClaimEventOutboxDrainer _drainer;
    private readonly ClaimEventOutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaimEventOutboxDispatcher> _logger;
    private readonly IConfiguration? _configuration;

    public ClaimEventOutboxDispatcher(
        IServiceScopeFactory scopes, ClaimEventOutboxDrainer drainer, IOptions<ClaimEventOutboxOptions> options,
        ILogger<ClaimEventOutboxDispatcher> logger, TimeProvider? clock = null, IConfiguration? configuration = null)
    {
        _scopes = scopes;
        _drainer = drainer;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _configuration = configuration;
    }

    /// <summary>
    /// Fails start-up when the dispatcher would run over tenant-scoped
    /// databases: a tenant database's pending events would never be retried.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled && _drainer.IsEnabled && _configuration?.GetValue(TenantScopingSetting, false) == true)
            throw new InvalidOperationException(
                $"{TenantScopingSetting} is on, but the claim-event outbox is not supported over tenant-scoped databases yet: " +
                "its dispatcher scans only the base claims database, so a tenant database's pending claims.finalized.v1 " +
                "events would never be retried. Turn tenant scoping off, or set Claims:EventOutbox:Enabled=false to start " +
                "without the dispatcher (events are then published inline only; one Kafka does not acknowledge stays " +
                "pending on the claim and is not retried).");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_drainer.IsEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Claim-event outbox dispatch failed; retrying next poll");
            }
            try
            {
                await Task.Delay(_options.PollInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One poll: drain every claim with a due event; returns how many events were published.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimRepository>();
        var due = await claims.FindDueClaimEventsAsync(
            AccumulatorOutboxItem.ToMs(_clock.GetUtcNow().UtcDateTime), _options.BatchSize, ct);

        var published = 0;
        foreach (var claim in due)
            published += await _drainer.DrainAsync(claims, claim, respectDueTimes: true, ct);

        var oldest = await claims.OldestClaimEventAsync(ct);
        var age = oldest is { } o ? _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(o) : (TimeSpan?)null;
        ClaimEventOutboxMetrics.SetOldestAge(age);
        if (age is { } a && a > _options.AgeAlert)
            _logger.LogError("Claim-event outbox: the oldest pending event is {Age} old (alert above {Alert})", a, _options.AgeAlert);
        return published;
    }
}
