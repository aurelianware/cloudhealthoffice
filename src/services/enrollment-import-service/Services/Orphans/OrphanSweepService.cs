using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Repositories;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Services.Orphans;

public sealed class OrphanSweepOptions
{
    public const string SectionName = "EnrollmentImport:OrphanSweep";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>Most orphans one tenant gets per sweep; the rest wait for the next sweep.</summary>
    public int BatchSize { get; set; } = 200;
}

public sealed record OrphanSweepResult(int Tenants, int Retried, int Resolved);

/// <summary>
/// The backstop for orphaned dependents (enrollment-operations.md §10,
/// decision 5). The subscriber's arrival is the normal trigger; when a grace
/// period ends, the sweep retries the orphan once more (catching an arrival
/// the trigger missed) and, if the subscriber is still not on file, opens it
/// to the Matching queue with its history. An orphan the sweep cannot settle
/// stays held past its date, which the work queue already treats as workable.
/// </summary>
public sealed class OrphanSweepService(
    IServiceScopeFactory scopes,
    IOptions<OrphanSweepOptions> options,
    TimeProvider clock,
    ILogger<OrphanSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Orphaned-dependent sweep failed; it runs again in {Minutes} minutes", interval.TotalMinutes);
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<OrphanSweepResult> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var exceptions = scope.ServiceProvider.GetRequiredService<IEnrollmentExceptionRepository>();
        var retrier = scope.ServiceProvider.GetRequiredService<OrphanDependentRetrier>();
        var now = clock.GetUtcNow().UtcDateTime;

        var tenants = await exceptions.ListTenantsWithExpiredAutoHeldAsync(now, ct);
        int retried = 0, resolved = 0;
        foreach (var tenantId in tenants)
        {
            var expired = await exceptions.ListExpiredAutoHeldAsync(tenantId, now, Math.Max(1, options.Value.BatchSize), ct);
            var tenantResolved = 0;
            foreach (var orphan in expired)
            {
                retried++;
                if (await retrier.RetryAsync(tenantId, orphan.Id, "Grace period ended: final automatic retry before the Matching queue.", ct))
                    tenantResolved++;
            }
            resolved += tenantResolved;
            if (expired.Count > 0)
                logger.LogInformation("Orphan sweep for tenant {TenantId}: {Ended} grace period(s) ended, {Resolved} resolved",
                    LogSanitizer.Sanitize(tenantId), expired.Count, tenantResolved);
        }
        return new OrphanSweepResult(tenants.Count, retried, resolved);
    }
}
