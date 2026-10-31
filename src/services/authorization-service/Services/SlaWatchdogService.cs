using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Retention;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Services;

/// <summary>
/// Background service that periodically evaluates open authorizations
/// and escalates SLA levels as deadlines approach or are breached.
///
/// <para>
/// A hosted service has no request, so no ambient tenant (D-07). Each tick
/// lists the tenants that have authorization data and evaluates them one at a
/// time, passing the tenant EXPLICITLY to every repository call, in a fresh
/// DI scope per tenant (the repository is Scoped; nothing is captured for the
/// process lifetime). Modelled on <see cref="PriorAuthorizationRetentionWorker"/>.
/// One tenant's failure, or one record's, is logged and does not stop the rest.
/// </para>
///
/// <para>
/// The tenant list is cached for <see cref="TenantListRefreshInterval"/>:
/// listing tenants is a collection scan (D-37), and tenants appear rarely. A new tenant's
/// authorizations are first evaluated within that interval.
/// </para>
///
/// <para>
/// Not supported with <c>MongoDb:UseTenantScoping=true</c> (a database per
/// tenant): with no request the repository sees only the base database, so
/// the watchdog would silently find nothing. It logs a warning and does
/// nothing instead (D-38).
/// </para>
/// </summary>
public class SlaWatchdogService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>How long a listed set of tenants is reused before it is listed again.</summary>
    public static readonly TimeSpan TenantListRefreshInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlaWatchdogService> _logger;
    private readonly TimeProvider _clock;
    private readonly string? _unsupportedReason;

    private IReadOnlyList<string>? _tenants;
    private DateTimeOffset _tenantsListedAt;

    public SlaWatchdogService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SlaWatchdogService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        if (configuration.GetValue("MongoDb:UseTenantScoping", false))
        {
            _unsupportedReason =
                "MongoDb:UseTenantScoping=true is not supported by the SLA watchdog: with no request it can " +
                "reach only the base database, not each tenant's. No authorization will be escalated (D-38).";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_unsupportedReason is not null)
        {
            // Say so once, loudly, rather than run every 15 minutes and find nothing.
            _logger.LogWarning("SLA watchdog disabled: {Reason}", _unsupportedReason);
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await EvaluateAllAuthorizationsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SLA watchdog evaluation failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Evaluates every tenant's open authorizations and escalates SLA levels.
    /// Public to allow direct invocation from tests.
    /// </summary>
    public async Task<SlaWatchdogRunSummary> EvaluateAllAuthorizationsAsync(CancellationToken ct = default)
    {
        var summary = new SlaWatchdogRunSummary();

        if (_unsupportedReason is not null)
        {
            _logger.LogWarning("SLA watchdog disabled: {Reason}", _unsupportedReason);
            summary.SkippedReason = _unsupportedReason;
            return summary;
        }

        var tenants = await GetTenantsAsync(ct);

        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            summary.Tenants++;

            // A scope per tenant: one tenant's work never shares state with another's.
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();

            try
            {
                await EvaluateTenantAsync(repository, tenantId, summary, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.FailedTenants++;
                _logger.LogError(ex, "SLA watchdog evaluation failed for tenant {Tenant}", LogSanitizer.Sanitize(tenantId));
            }
        }

        return summary;
    }

    private async Task<IReadOnlyList<string>> GetTenantsAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_tenants is not null && now - _tenantsListedAt < TenantListRefreshInterval)
            return _tenants;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
        _tenants = await repository.ListTenantIdsAsync(ct);
        _tenantsListedAt = now;
        return _tenants;
    }

    private async Task EvaluateTenantAsync(
        IAuthorizationRepository repository, string tenantId, SlaWatchdogRunSummary summary, CancellationToken ct)
    {
        var auths = await repository.GetOpenAuthorizationsAsync(tenantId);

        foreach (var auth in auths)
        {
            ct.ThrowIfCancellationRequested();

            // Strict isolation: act only on records of the tenant being evaluated.
            if (!string.Equals(auth.TenantId, tenantId, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "SLA watchdog skipped authorization {AuthId}: listed for tenant {Tenant} but belongs to another tenant",
                    LogSanitizer.Sanitize(auth.Id), LogSanitizer.Sanitize(tenantId));
                continue;
            }

            // An imported request is decided in the source system; CHO has no deadline to keep for it.
            if (auth.IsImported)
                continue;

            if (!auth.Status.IsOpen())
                continue;

            summary.Evaluated++;

            try
            {
                await EvaluateRecordAsync(repository, tenantId, auth, summary, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad record never stops the rest of the tenant.
                summary.FailedRecords++;
                _logger.LogError(ex,
                    "SLA watchdog evaluation failed for authorization {AuthId} (tenant {Tenant})",
                    LogSanitizer.Sanitize(auth.Id), LogSanitizer.Sanitize(tenantId));
            }
        }
    }

    private async Task EvaluateRecordAsync(
        IAuthorizationRepository repository, string tenantId, Authorization auth,
        SlaWatchdogRunSummary summary, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if ((!auth.DecisionReceivedAt.HasValue || !auth.DecisionDueAt.HasValue)
            && !await repository.EnsureDecisionDeadlineAsync(tenantId, auth, ct))
            return; // A concurrent writer won; re-read on the next tick.
        var slaStart = AuthorizationDeadlinePolicy.Receipt(auth);
        var elapsed = (now - slaStart).TotalHours;
        var isUrgent = AuthorizationDeadlinePolicy.IsExpedited(auth.LevelOfService);
        var remaining = (AuthorizationDeadlinePolicy.Deadline(auth) - now).TotalHours;
        var newLevel = ComputeEscalationLevel(remaining, isUrgent);

        if (newLevel <= auth.SlaEscalation)
            return;

        // Conditional on the tenant, the record still being open and its
        // level still being lower; writes only the escalation fields.
        if (!await repository.RaiseSlaEscalationAsync(tenantId, auth.Id, newLevel, now, ct))
            return;

        summary.Escalated++;
        _logger.LogWarning(
            "SLA {Level} for authorization {AuthNumber} (tenant {Tenant}): {HoursElapsed:F1}h elapsed, {HoursRemaining:F1}h remaining",
            newLevel, LogSanitizer.Sanitize(auth.AuthorizationNumber), LogSanitizer.Sanitize(tenantId), elapsed, remaining);
    }

    /// <summary>
    /// Computes the SLA status DTO for a single authorization (used by the endpoint).
    /// </summary>
    public static AuthorizationSlaStatus ComputeSlaStatus(Authorization auth, DateTime? nowUtc = null)
    {
        var slaStart = AuthorizationDeadlinePolicy.Receipt(auth);
        var due = AuthorizationDeadlinePolicy.Deadline(auth);
        var isUrgent = AuthorizationDeadlinePolicy.IsExpedited(auth.LevelOfService);
        var now = nowUtc ?? DateTime.UtcNow;
        var elapsed = (now - slaStart).TotalHours;
        var window = (due - slaStart).TotalHours;
        var remaining = (due - now).TotalHours;
        var percentConsumed = window > 0 ? elapsed / window * 100 : 100;
        var escalation = ComputeEscalationLevel(remaining, isUrgent);

        return new AuthorizationSlaStatus
        {
            Id = auth.Id,
            AuthorizationNumber = auth.AuthorizationNumber,
            MemberId = auth.MemberId,
            TenantId = auth.TenantId,
            Status = auth.Status,
            LevelOfService = auth.LevelOfService,
            SlaStartedAt = slaStart,
            SlaDeadline = due,
            HoursElapsed = Math.Round(elapsed, 1),
            HoursRemaining = Math.Round(remaining, 1),
            PercentConsumed = Math.Round(percentConsumed, 1),
            EscalationLevel = escalation,
        };
    }

    private static SlaEscalationLevel ComputeEscalationLevel(double remaining, bool isUrgent)
    {
        if (remaining <= 0) return SlaEscalationLevel.Breach;
        if (remaining <= (isUrgent ? 8 : 24)) return SlaEscalationLevel.Critical;
        if (remaining <= (isUrgent ? 24 : 48)) return SlaEscalationLevel.Warning;
        return SlaEscalationLevel.None;
    }

}

/// <summary>Counts from one watchdog run. Aggregate only; no member identifiers.</summary>
public sealed class SlaWatchdogRunSummary
{
    /// <summary>Tenants evaluated.</summary>
    public int Tenants { get; set; }

    /// <summary>Tenants whose evaluation failed (logged; the others still ran).</summary>
    public int FailedTenants { get; set; }

    /// <summary>Open authorizations evaluated.</summary>
    public int Evaluated { get; set; }

    /// <summary>Escalations recorded.</summary>
    public int Escalated { get; set; }

    /// <summary>Records whose evaluation failed (logged; the rest of the tenant still ran).</summary>
    public int FailedRecords { get; set; }

    /// <summary>Why the run did nothing at all (an unsupported configuration), or null.</summary>
    public string? SkippedReason { get; set; }
}
