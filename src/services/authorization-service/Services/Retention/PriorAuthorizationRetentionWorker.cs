using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Services.Retention;

/// <summary>
/// Sweeps expired prior-authorization records, tenant by tenant.
///
/// The job DISCOVERS eligibility; it does not define it. The boundary is a
/// property of the record and the policy, so a sweep that runs late, early, or
/// twice reaches the same answer — correctness never depends on the cadence.
///
/// Modelled on provider-service's IntegrityProjectionWorker: a scope per tenant
/// from IServiceScopeFactory, an Enabled gate, a bounded batch, and cancellation
/// observed between every unit of work. Deliberately NOT modelled on this
/// service's SlaWatchdogService, which captures a scoped repository for the
/// process lifetime and calls a tenant-less repository method from a background
/// thread — both of which this worker avoids.
/// </summary>
public sealed class PriorAuthorizationRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<PriorAuthorizationRetentionOptions> _options;
    private readonly ILogger<PriorAuthorizationRetentionWorker> _logger;

    public PriorAuthorizationRetentionWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<PriorAuthorizationRetentionOptions> options,
        ILogger<PriorAuthorizationRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;

            if (!options.Enabled)
            {
                // Off by default: a destructive sweep opts in.
                await DelayAsync(options.SweepInterval, stoppingToken);
                continue;
            }

            try
            {
                await SweepAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep is retried on the next tick. Nothing is left
                // half-purged: each record is an independent conditional delete.
                _logger.LogError(ex, "Prior-authorization retention sweep failed; will retry next interval.");
            }

            await DelayAsync(options.SweepInterval, stoppingToken);
        }
    }

    /// <summary>One pass over every tenant. Public for direct test invocation.</summary>
    public async Task<RetentionSweepSummary> SweepAsync(
        PriorAuthorizationRetentionOptions options, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var summary = new RetentionSweepSummary { RunId = runId };

        IReadOnlyList<string> tenants;
        using (var bootScope = _scopeFactory.CreateScope())
        {
            var repository = bootScope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
            tenants = await repository.ListTenantIdsAsync(ct);
        }

        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();

            // A scope per tenant: the repository is Scoped, and one tenant's
            // work must never share state with another's.
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
            var policy = scope.ServiceProvider.GetRequiredService<IPriorAuthorizationRetentionPolicy>();

            var tenantSummary = await SweepTenantAsync(repository, policy, options, tenantId, runId, ct);
            summary.Add(tenantSummary);
        }

        _logger.LogInformation(
            "Prior-authorization retention sweep {RunId} complete: tenants={Tenants} scanned={Scanned} "
            + "purged={Purged} skipped={Skipped} failed={Failed} dryRun={DryRun}",
            runId, tenants.Count, summary.Scanned, summary.Purged, summary.Skipped, summary.Failed,
            options.DryRun);

        return summary;
    }

    private async Task<RetentionSweepSummary> SweepTenantAsync(
        IAuthorizationRepository repository,
        IPriorAuthorizationRetentionPolicy policy,
        PriorAuthorizationRetentionOptions options,
        string tenantId,
        string runId,
        CancellationToken ct)
    {
        var summary = new RetentionSweepSummary { RunId = runId };
        var asOf = DateTime.UtcNow;

        var candidates = await repository.FindRetentionCandidatesAsync(
            tenantId, policy.CandidateCutoffUtc(asOf), options.MaxRecordsPerTenantPerSweep, ct);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            summary.Scanned++;

            // The query is a coarse pre-filter; THIS is the decision. A record
            // the query returned but the policy declines is skipped, not purged.
            if (!policy.IsPurgeEligible(candidate, asOf))
            {
                summary.Skipped++;
                continue;
            }

            if (options.DryRun)
            {
                summary.WouldPurge++;
                RecordOutcome(tenantId, "would_purge", options.DryRun);
                continue;
            }

            try
            {
                // Conditional on the status still being what we decided against,
                // so a record that reopened between listing and deleting survives.
                var purged = await repository.PurgeIfStillEligibleAsync(
                    tenantId, candidate.Id, candidate.Status, ct);

                if (purged)
                {
                    summary.Purged++;
                    RecordOutcome(tenantId, "purged", options.DryRun);

                    // Per-record audit: opaque ids and categories only. No member
                    // identity, no clinical content, no denial narrative.
                    _logger.LogInformation(
                        "Prior-authorization purged: run={RunId} tenant={Tenant} authorization={AuthId} "
                        + "policy={Policy} retainedUntil={RetainedUntil} status={Status}",
                        runId, Sanitize(tenantId), Sanitize(candidate.Id),
                        policy.PolicyVersion, policy.RetentionUntilUtc(candidate), candidate.Status);
                }
                else
                {
                    // Already gone, or it changed under us. Both are fine.
                    summary.Skipped++;
                    RecordOutcome(tenantId, "skipped", options.DryRun);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                RecordOutcome(tenantId, "failed", options.DryRun);
                _logger.LogWarning(ex,
                    "Prior-authorization purge failed: run={RunId} tenant={Tenant} authorization={AuthId}",
                    runId, Sanitize(tenantId), Sanitize(candidate.Id));
            }
        }

        return summary;
    }

    private static void RecordOutcome(string tenantId, string outcome, bool dryRun)
        => ChoMetrics.PriorAuthorizationRetentionOutcomes.Add(1,
            new KeyValuePair<string, object?>("cho.outcome", outcome),
            new KeyValuePair<string, object?>("cho.dry_run", dryRun ? "true" : "false"),
            new KeyValuePair<string, object?>("cho.tenant_id", tenantId));

    /// <summary>Swallows cancellation so shutdown is clean rather than noisy.</summary>
    private static async Task DelayAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Strips CR/LF so an id cannot forge a log entry (CWE-117).</summary>
    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// Counts from a sweep. Aggregate by design — normal operation logs totals, not
/// a line per record, and carries no member or clinical identifiers.
/// </summary>
public sealed class RetentionSweepSummary
{
    public string RunId { get; init; } = string.Empty;

    /// <summary>Candidates examined.</summary>
    public int Scanned { get; set; }

    /// <summary>Records actually deleted.</summary>
    public int Purged { get; set; }

    /// <summary>Eligible in a dry run, and therefore not deleted.</summary>
    public int WouldPurge { get; set; }

    /// <summary>Declined by the policy, already gone, or changed under the sweep.</summary>
    public int Skipped { get; set; }

    public int Failed { get; set; }

    public void Add(RetentionSweepSummary other)
    {
        Scanned += other.Scanned;
        Purged += other.Purged;
        WouldPurge += other.WouldPurge;
        Skipped += other.Skipped;
        Failed += other.Failed;
    }
}
