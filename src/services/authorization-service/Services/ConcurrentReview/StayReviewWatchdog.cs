using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Services.ConcurrentReview;

/// <summary>What one watchdog pass found.</summary>
public sealed class StayReviewWatchdogSummary
{
    public int Tenants { get; set; }
    public int FailedTenants { get; set; }
    public int OpenStays { get; set; }

    /// <summary>Open stays whose next review date has passed.</summary>
    public int OverdueReviews { get; set; }

    /// <summary>Open stays whose certification has run out behind the member.</summary>
    public int LapsedCertifications { get; set; }

    public string? SkippedReason { get; set; }
}

/// <summary>
/// Periodically reports open stays whose review is overdue or whose
/// certification has lapsed.
///
/// It changes nothing. A lapsed certification is a clinical and contractual
/// matter — days are certified by a reviewer, never by a timer — so the
/// watchdog's job is to make sure nobody has to notice by reading the census.
/// It logs a warning per tenant with a count and the stays behind it, which is
/// what alerting hangs off.
///
/// <para>
/// A hosted service has no request and so no ambient tenant: each pass lists
/// the tenants that have stays and passes the tenant EXPLICITLY, in a fresh DI
/// scope per tenant, exactly as <see cref="SlaWatchdogService"/> does. One
/// tenant's failure does not stop the rest.
/// </para>
/// </summary>
public sealed class StayReviewWatchdog : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StayReviewWatchdog> _logger;
    private readonly TimeProvider _clock;
    private readonly string? _unsupportedReason;

    public StayReviewWatchdog(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<StayReviewWatchdog> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        if (configuration.GetValue("MongoDb:UseTenantScoping", false))
        {
            _unsupportedReason =
                "MongoDb:UseTenantScoping=true is not supported by the stay review watchdog: with no request it " +
                "can reach only the base database, not each tenant's. No stay will be reported (D-38).";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_unsupportedReason is not null)
        {
            // Say so once, loudly, rather than run every hour and find nothing.
            _logger.LogWarning("Stay review watchdog disabled: {Reason}", _unsupportedReason);
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await EvaluateAllStaysAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stay review watchdog evaluation failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Evaluates every tenant's open stays. Public so tests can run one pass.</summary>
    public async Task<StayReviewWatchdogSummary> EvaluateAllStaysAsync(CancellationToken ct = default)
    {
        var summary = new StayReviewWatchdogSummary();

        if (_unsupportedReason is not null)
        {
            summary.SkippedReason = _unsupportedReason;
            return summary;
        }

        using var listingScope = _scopeFactory.CreateScope();
        var tenants = await listingScope.ServiceProvider
            .GetRequiredService<IInpatientStayRepository>()
            .ListTenantIdsAsync(ct);

        var asOf = _clock.GetUtcNow().UtcDateTime;

        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            summary.Tenants++;

            // A scope per tenant: one tenant's work never shares state with another's.
            using var scope = _scopeFactory.CreateScope();
            var stays = scope.ServiceProvider.GetRequiredService<IInpatientStayRepository>();

            try
            {
                await EvaluateTenantAsync(stays, tenantId, asOf, summary, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.FailedTenants++;
                _logger.LogError(ex, "Stay review watchdog failed for tenant {Tenant}", LogSanitizer.Sanitize(tenantId));
            }
        }

        return summary;
    }

    private async Task EvaluateTenantAsync(
        IInpatientStayRepository stays, string tenantId, DateTime asOf,
        StayReviewWatchdogSummary summary, CancellationToken ct)
    {
        var open = await stays.ListOpenForTenantAsync(tenantId, ct);
        summary.OpenStays += open.Count;

        var overdue = open.Where(s => StayReviewPolicy.IsReviewOverdue(s, asOf)).ToList();
        var lapsed = open.Where(s => StayReviewPolicy.IsCertificationLapsed(s, asOf)).ToList();

        summary.OverdueReviews += overdue.Count;
        summary.LapsedCertifications += lapsed.Count;

        if (overdue.Count > 0)
            _logger.LogWarning(
                "Tenant {Tenant}: {Count} inpatient stay(s) past their concurrent review date: {StayIds}",
                LogSanitizer.Sanitize(tenantId), overdue.Count,
                LogSanitizer.Sanitize(string.Join(", ", overdue.Select(s => s.Id))));

        if (lapsed.Count > 0)
            _logger.LogWarning(
                "Tenant {Tenant}: {Count} inpatient stay(s) on uncertified days: {StayIds}",
                LogSanitizer.Sanitize(tenantId), lapsed.Count,
                LogSanitizer.Sanitize(string.Join(", ", lapsed.Select(s => s.Id))));
    }
}
