using System.Diagnostics.Metrics;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Adjudication;

/// <summary>Settings for the accumulator outbox (<c>Claims:AccumulatorOutbox</c>).</summary>
public sealed class AccumulatorOutboxOptions
{
    public const string SectionName = "Claims:AccumulatorOutbox";

    /// <summary>Run the background dispatcher (the request path always attempts once).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
    public int BatchSize { get; set; } = 50;

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>An outbox entry older than this is logged at error on every poll (alert on it).</summary>
    public TimeSpan AgeAlert { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>What one outbox attempt did.</summary>
public enum AccumulatorOutboxResult
{
    /// <summary>Nothing (no entry, or not the expected one).</summary>
    NothingDue,
    Committed,
    AlreadyCommitted,
    /// <summary>The store refused the commit: the claim id is fenced (voided / denied).</summary>
    Refused,
    Reversed,
    /// <summary>
    /// The claim is Denied or Voided, or the version was superseded by a
    /// replacement: the commit is dropped, never written.
    /// </summary>
    Dropped,
    /// <summary>The engine call failed; rescheduled with backoff.</summary>
    Rescheduled,
    /// <summary>
    /// A commit-scoped reversal found a newer commit of the claim applied
    /// (it went back to Pay): stale, nothing changed; the entry is cleared.
    /// </summary>
    Superseded,
}

/// <summary>Metrics of the accumulator outbox (meter <see cref="ChoMetrics.MeterName"/>).</summary>
public static class AccumulatorOutboxMetrics
{
    private static readonly Meter Meter = new(ChoMetrics.MeterName);
    private static long _oldestAgeSeconds;

    /// <summary>Attempts by <c>kind</c> (commit / reversal) and <c>result</c>.</summary>
    public static readonly Counter<long> Attempts = Meter.CreateCounter<long>(
        "cho.claims.accumulator_outbox.attempts.total", unit: "{attempt}",
        description: "Accumulator outbox attempts by kind and result");

    /// <summary>Age of the oldest outstanding entry (alert when it grows).</summary>
    public static readonly ObservableGauge<long> OldestAge = Meter.CreateObservableGauge(
        "cho.claims.accumulator_outbox.oldest_age", () => Interlocked.Read(ref _oldestAgeSeconds), unit: "s",
        description: "Age of the oldest pending accumulator outbox entry");

    public static void SetOldestAge(TimeSpan? age) =>
        Interlocked.Exchange(ref _oldestAgeSeconds, age is { } a ? (long)a.TotalSeconds : 0);

    public static long OldestAgeSeconds => Interlocked.Read(ref _oldestAgeSeconds);
}

/// <summary>
/// Drives one claim's accumulator outbox entry: the engine commit a finally
/// adjudicated claim owes (<see cref="Claim.PendingAccumulatorCommit"/>) or
/// the terminal reversal a denial owes (<see cref="Claim.PendingAccumulatorReversal"/>).
/// Idempotent: the commit is keyed on its commit id in the store, the
/// reversal is idempotent, and the entry is cleared only while it is still
/// the one that was driven. A failure reschedules it with backoff; the
/// <see cref="AccumulatorOutboxDispatcher"/> picks it up again.
/// </summary>
public interface IAccumulatorOutboxProcessor
{
    /// <param name="expectedItemId">When set, act only if the entry is still this one (the request path's immediate attempt).</param>
    Task<AccumulatorOutboxResult> ProcessAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string? expectedItemId, CancellationToken ct);
}

public sealed class AccumulatorOutboxProcessor : IAccumulatorOutboxProcessor
{
    private readonly IClaimRepository _claims;
    private readonly IBenefitCalculationEngine _engine;
    private readonly IAdjudicationTenantContext _tenantContext;
    private readonly AccumulatorOutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccumulatorOutboxProcessor> _logger;

    public AccumulatorOutboxProcessor(
        IClaimRepository claims,
        IBenefitCalculationEngine engine,
        IAdjudicationTenantContext tenantContext,
        IOptions<AccumulatorOutboxOptions> options,
        ILogger<AccumulatorOutboxProcessor> logger,
        TimeProvider? clock = null)
    {
        _claims = claims;
        _engine = engine;
        _tenantContext = tenantContext;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<AccumulatorOutboxResult> ProcessAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string? expectedItemId, CancellationToken ct)
    {
        var claim = await _claims.GetForAccumulatorOutboxAsync(tenantId, claimId, ct);
        var item = kind == AccumulatorOutboxKind.Commit ? claim?.PendingAccumulatorCommit : claim?.PendingAccumulatorReversal;
        if (claim is null || item is null || (expectedItemId is not null && item.Id != expectedItemId))
            return AccumulatorOutboxResult.NothingDue;

        // The engine client sends X-Tenant-ID from here (no request context).
        _tenantContext.TenantId = tenantId;
        var kindTag = kind == AccumulatorOutboxKind.Commit ? "commit" : "reversal";
        AccumulatorOutboxResult result;
        try
        {
            result = kind == AccumulatorOutboxKind.Commit
                ? await CommitAsync(claim, item, ct)
                : await ReverseAsync(item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var attempts = item.Attempts + 1;
            var factor = Math.Pow(2, Math.Min(attempts - 1, 20));
            var delay = TimeSpan.FromTicks((long)Math.Min(_options.BaseBackoff.Ticks * factor, _options.MaxBackoff.Ticks));
            var next = _clock.GetUtcNow().UtcDateTime + delay;
            // Error text: exception type and HTTP status only — no claim data.
            var error = ex is HttpRequestException http && http.StatusCode is { } code
                ? $"{ex.GetType().Name} {(int)code}"
                : ex.GetType().Name;
            await _claims.RescheduleAccumulatorOutboxAsync(tenantId, claimId, kind, item.Id, attempts, next, error, CancellationToken.None);
            _logger.LogWarning(ex,
                "Accumulator outbox {Kind} {ItemId} for claim {ClaimId} failed (attempt {Attempts}); next attempt at {Next:o}",
                kindTag, LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claimId), attempts, next);
            result = AccumulatorOutboxResult.Rescheduled;
        }
        AccumulatorOutboxMetrics.Attempts.Add(1,
            new KeyValuePair<string, object?>("kind", kindTag),
            new KeyValuePair<string, object?>("result", result.ToString()));
        return result;
    }

    private async Task<AccumulatorOutboxResult> CommitAsync(Claim claim, AccumulatorOutboxItem item, CancellationToken ct)
    {
        var dropReason = ClaimStatusSemantics.IsDenied(claim.Status) || claim.Status == ClaimStatus.Voided
            // Adjudicated as Pay, then denied (Deny, or finalized Denied) or
            // voided before the commit ran: it must never land (the void /
            // denial reversal fences it in the store as well). Denials write
            // no accumulators in either phase.
            ? $"the claim is {claim.Status}"
            : claim.VersionState == ClaimVersionState.Adjusted || claim.SupersededAt is not null
                // A replacement superseded this version before its commit ran
                // (e.g. benefit-plan-service was down): the replacement's own
                // commit counts the claim; this one would count it twice. (The
                // replacement's commit also fences this claim id in the store,
                // for a commit of it already in flight.)
                ? $"the version was superseded by {claim.SupersededByVersionId ?? "a replacement"}"
                : null;
        if (dropReason is not null)
        {
            _logger.LogWarning(
                "AUDIT accumulator commit {ItemId} for claim {ClaimId} dropped, never written: {Reason}",
                LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id), LogSanitizer.Sanitize(dropReason));
            await _claims.CompleteAccumulatorOutboxAsync(claim.TenantId, claim.Id, AccumulatorOutboxKind.Commit, item.Id, ct: CancellationToken.None);
            return AccumulatorOutboxResult.Dropped;
        }

        var commit = item.Commit ?? throw new InvalidOperationException($"Outbox entry {item.Id} holds no commit.");
        var result = await _engine.CommitAccumulatorsAsync(commit, ct);

        // A review is saved in its own write keyed on the commit id — before
        // the slot is cleared and whatever happens to the slot — so the
        // attempt that learns the store's answer always records it (a repeat
        // answers AlreadyCommitted with the first attempt's clamps).
        AccumulatorClampReview? review = null;
        if (result.Clamped.Count > 0)
        {
            // The store took less than the pricing saw (a concurrent claim
            // for the member or family reached the limit first). The paid
            // amounts are not changed automatically: an examiner reviews it.
            review = new AccumulatorClampReview
            {
                CommitId = commit.CommitId,
                Kind = AccumulatorClampReview.Clamped,
                RaisedAt = _clock.GetUtcNow().UtcDateTime,
                Clamps = result.Clamped,
            };
            _logger.LogWarning(
                "Accumulator commit {ItemId} for claim {ClaimId}: {Count} update(s) clamped at a limit; adjustment review raised",
                LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id), result.Clamped.Count);
        }
        else if (result.Outcome == AccumulatorCommitOutcome.RefusedClaimReversed)
        {
            // The store fences this claim id (voided, denied, or replaced) but
            // the claim reads as finalized here: its paid amounts were priced
            // with a cost share the member's accumulators do not hold.
            review = new AccumulatorClampReview
            {
                CommitId = commit.CommitId,
                Kind = AccumulatorClampReview.Refused,
                RaisedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _logger.LogError(
                "Accumulator commit {ItemId} for claim {ClaimId} refused: the claim id is fenced in the store while the claim is {Status}; review raised",
                LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(claim.Id), claim.Status);
        }
        if (review is not null)
            await _claims.RecordAccumulatorReviewAsync(claim.TenantId, claim.Id, review, CancellationToken.None);

        await _claims.CompleteAccumulatorOutboxAsync(claim.TenantId, claim.Id, AccumulatorOutboxKind.Commit, item.Id, ct: CancellationToken.None);
        return result.Outcome switch
        {
            AccumulatorCommitOutcome.Committed => AccumulatorOutboxResult.Committed,
            AccumulatorCommitOutcome.AlreadyCommitted => AccumulatorOutboxResult.AlreadyCommitted,
            // That commit was reversed while in flight (the claim was denied
            // meanwhile): it must not land, and it is not an error.
            AccumulatorCommitOutcome.RefusedCommitReversed => AccumulatorOutboxResult.Dropped,
            _ => AccumulatorOutboxResult.Refused,
        };
    }

    private async Task<AccumulatorOutboxResult> ReverseAsync(AccumulatorOutboxItem item, CancellationToken ct)
    {
        var order = item.Reversal ?? throw new InvalidOperationException($"Outbox entry {item.Id} holds no reversal.");
        if (order.Terminal)
            await _engine.ReverseClaimAsync(
                order.MemberId, order.SubscriberId, Guid.Parse(order.BenefitPlanId),
                DateOnly.FromDateTime(order.ServiceDate), order.ClaimId, ct);
        else
        {
            // A Pay claim moved to Deny: compare-and-reverse the commit it
            // names (no claim fence — it may go back to Pay and commit again).
            // A newer commit already applied makes this a no-op.
            var outcome = await _engine.ReverseClaimForReprocessingAsync(
                order.MemberId, order.SubscriberId, Guid.Parse(order.BenefitPlanId),
                DateOnly.FromDateTime(order.ServiceDate), order.ClaimId, order.EngineCommitId, ct);
            await _claims.CompleteAccumulatorOutboxAsync(
                _tenantContext.TenantId!, order.ClaimId, AccumulatorOutboxKind.Reversal, item.Id, ct: CancellationToken.None);
            if (outcome == AccumulatorReversalOutcome.Superseded)
            {
                _logger.LogInformation(
                    "Accumulator reversal {ItemId} of commit {CommitId} for claim {ClaimId} superseded: a newer commit is applied",
                    LogSanitizer.Sanitize(item.Id), LogSanitizer.Sanitize(order.CommitId), LogSanitizer.Sanitize(order.ClaimId));
                return AccumulatorOutboxResult.Superseded;
            }
            return AccumulatorOutboxResult.Reversed;
        }
        await _claims.CompleteAccumulatorOutboxAsync(
            _tenantContext.TenantId!, order.ClaimId, AccumulatorOutboxKind.Reversal, item.Id, ct: CancellationToken.None);
        return AccumulatorOutboxResult.Reversed;
    }
}

/// <summary>
/// Background dispatcher of the accumulator outbox: every
/// <see cref="AccumulatorOutboxOptions.PollInterval"/> it drives the due
/// entries of every tenant (each in its own scope), updates the oldest-age
/// gauge, and logs an error while an entry is older than
/// <see cref="AccumulatorOutboxOptions.AgeAlert"/>. Several replicas may run
/// it: every step is idempotent and the clear is conditional on the entry id.
/// </summary>
public sealed class AccumulatorOutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AccumulatorOutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AccumulatorOutboxDispatcher> _logger;

    private readonly IConfiguration? _configuration;

    public AccumulatorOutboxDispatcher(
        IServiceScopeFactory scopes, IOptions<AccumulatorOutboxOptions> options,
        ILogger<AccumulatorOutboxDispatcher> logger, TimeProvider? clock = null, IConfiguration? configuration = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        var scope = AccumulatorOutboxScopeHealthCheck.Evaluate(
            _configuration?.GetValue(AccumulatorOutboxScopeHealthCheck.TenantScopingSetting, false) == true, _options.Enabled);
        if (scope.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
            _logger.LogCritical("Accumulator outbox: {Problem}", scope.Description);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Accumulator outbox dispatch failed; retrying next poll");
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

    /// <summary>One poll: drive every due entry; returns how many were attempted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Claim> due;
        long? oldest;
        using (var scope = _scopes.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IClaimRepository>();
            due = await claims.FindDueAccumulatorOutboxAsync(
                AccumulatorOutboxItem.ToMs(_clock.GetUtcNow().UtcDateTime), _options.BatchSize, ct);
            oldest = await claims.OldestAccumulatorOutboxAsync(ct);
        }

        var age = oldest is { } o ? _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(o) : (TimeSpan?)null;
        AccumulatorOutboxMetrics.SetOldestAge(age);
        if (age is { } a && a > _options.AgeAlert)
            _logger.LogError("Accumulator outbox: the oldest entry is {Age} old (alert above {Alert})", a, _options.AgeAlert);

        var attempted = 0;
        var nowMs = AccumulatorOutboxItem.ToMs(_clock.GetUtcNow().UtcDateTime);
        foreach (var claim in due ?? [])
        {
            foreach (var (kind, item) in new[]
                     {
                         (AccumulatorOutboxKind.Commit, claim.PendingAccumulatorCommit),
                         (AccumulatorOutboxKind.Reversal, claim.PendingAccumulatorReversal),
                     })
            {
                if (item is null || item.DueAtMs > nowMs) continue;
                // A scope per entry: the tenant context and repositories are scoped.
                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAccumulatorOutboxProcessor>();
                await processor.ProcessAsync(claim.TenantId, claim.Id, kind, item.Id, ct);
                attempted++;
            }
        }
        return attempted;
    }
}

/// <summary>
/// The dispatcher scans the claims database claims-service is configured
/// with. With tenant-scoped Mongo databases (<c>MongoDb:UseTenantScoping</c>)
/// a tenant's entries live in its own database, which it never scans: an
/// entry whose inline attempt failed would never be retried. Reported as
/// Degraded (readiness tag) and logged at critical on start-up.
/// </summary>
public sealed class AccumulatorOutboxScopeHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public const string Name = "accumulator-outbox-scope";
    public const string TenantScopingSetting = "MongoDb:UseTenantScoping";

    private readonly IConfiguration _configuration;
    private readonly AccumulatorOutboxOptions _options;

    public AccumulatorOutboxScopeHealthCheck(IConfiguration configuration, IOptions<AccumulatorOutboxOptions> options)
    {
        _configuration = configuration;
        _options = options.Value;
    }

    /// <summary>Healthy unless the dispatcher is enabled over tenant-scoped databases it cannot scan.</summary>
    public static Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Evaluate(bool tenantScoping, bool dispatcherEnabled) =>
        tenantScoping && dispatcherEnabled
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                "MongoDb:UseTenantScoping is on: the accumulator outbox dispatcher scans only the base claims database, " +
                "so a tenant database's failed accumulator commits and reversals are never retried.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();

    public Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult Evaluate() =>
        Evaluate(_configuration.GetValue(TenantScopingSetting, false), _options.Enabled);

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate());
}
