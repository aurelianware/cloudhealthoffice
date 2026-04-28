using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.HostedServices;

/// <summary>
/// Background sweeper that refreshes <c>Provider.IntegrityScore</c> on a
/// schedule (capability 5.4.5).
///
/// <para>
/// Pattern mirrors <c>PlanYearScheduler</c> in <c>benefit-plan-service</c>:
/// a <see cref="BackgroundService"/> that loops on a configurable
/// interval, scopes a fresh repository per tenant via
/// <see cref="IServiceScopeFactory"/>, and emits structured-log
/// telemetry per tenant per sweep.
/// </para>
///
/// <para>
/// The worker is gated by per-provider <see cref="Provider.NextVerificationDue"/>
/// — the sweep filter only returns providers actually due, so a shorter
/// sweep interval translates into faster responsiveness, not more work.
/// </para>
/// </summary>
public sealed class IntegrityProjectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<IntegrityProjectionOptions> _options;
    private readonly ILogger<IntegrityProjectionWorker> _logger;

    public IntegrityProjectionWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<IntegrityProjectionOptions> options,
        ILogger<IntegrityProjectionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IntegrityProjectionWorker started (Enabled={Enabled}, SweepInterval={Sweep}, PageSize={Page}, MaxPerSweep={Max})",
            _options.CurrentValue.Enabled,
            _options.CurrentValue.SweepInterval,
            _options.CurrentValue.PageSize,
            _options.CurrentValue.MaxProvidersPerTenantPerSweep);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.CurrentValue.Enabled)
                {
                    await DelayAsync(_options.CurrentValue.SweepInterval, stoppingToken);
                    continue;
                }

                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "IntegrityProjectionWorker sweep failed; retrying after {Interval}",
                    _options.CurrentValue.SweepInterval);
            }

            await DelayAsync(_options.CurrentValue.SweepInterval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var sweepStart = DateTimeOffset.UtcNow;

        // One scope per tenant so the scoped repository / projection
        // service get fresh dependencies. This matches the pattern used
        // by PlanYearScheduler in benefit-plan-service.
        IReadOnlyList<string> tenantIds;
        using (var bootScope = _scopeFactory.CreateScope())
        {
            var providers = bootScope.ServiceProvider.GetRequiredService<IProviderRepository>();
            tenantIds = await providers.ListProviderTenantIdsAsync(ct);
        }

        var totalPatched = 0;
        var totalFailed = 0;
        var totalSkipped = 0;

        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var projection = scope.ServiceProvider
                .GetRequiredService<IProviderIntegrityProjectionService>();

            var result = await projection.RefreshTenantAsync(
                tenantId,
                new IntegrityProjectionTenantSweepRequest
                {
                    DueBefore = sweepStart,
                    IncludeNeverVerified = true,
                    ActorId = "system:integrity-projection-worker",
                },
                ct);

            totalPatched += result.Patched;
            totalFailed += result.Failed;
            totalSkipped += result.Skipped;

            // Capability 5.10 — staleness telemetry piggybacks on the
            // existing sweep. The reporter updates the per-tenant
            // gauge snapshot read by ChoMetrics.ProviderIntegrityScoreStaleCount.
            // Decision 3: no new hosted service; we run the count once
            // per sweep cycle so the gauge reflects state at sweep
            // boundaries rather than chasing every projection write.
            // ReportTenantAsync returns -1 when the repository read
            // failed; we log "unknown" so operators don't read a zero
            // as "no stale providers".
            var staleness = scope.ServiceProvider
                .GetRequiredService<IIntegrityProjectionStalenessReporter>();
            var staleCount = await staleness.ReportTenantAsync(tenantId, ct);
            var staleLabel = staleCount < 0 ? "unknown" : staleCount.ToString();

            _logger.LogInformation(
                "IntegrityProjectionWorker tenant sweep: tenant={Tenant} inspected={Inspected} patched={Patched} skipped={Skipped} failed={Failed} stale={Stale} window={Window}",
                Sanitize(tenantId), result.Inspected, result.Patched,
                result.Skipped, result.Failed, staleLabel, result.RefreshWindow);
        }

        _logger.LogInformation(
            "IntegrityProjectionWorker sweep complete: tenants={Tenants} patched={Patched} skipped={Skipped} failed={Failed} duration={DurationMs}ms",
            tenantIds.Count, totalPatched, totalSkipped, totalFailed,
            (int)(DateTimeOffset.UtcNow - sweepStart).TotalMilliseconds);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
