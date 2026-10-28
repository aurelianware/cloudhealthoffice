using AppealsService.Repositories;
using AppealsService.Services;

namespace AppealsService.HostedServices;

/// <summary>
/// Delivers decided authorization appeals that the decision request could not
/// report (authorization-service down, a legacy record still awaiting
/// reconciliation) to the CMS-0057-F reporting ledger, with backoff per case
/// (<see cref="AppealDecisionReporting.Backoff"/>) until accepted, refused, or
/// given up after <see cref="AppealDecisionReporting.MaxAttempts"/>.
/// Interval: <c>AppealReporting:SweepInterval</c> (default five minutes).
/// </summary>
public sealed class AppealDecisionReportingSweep(IMemberServiceCaseStore store, AppealDecisionReporting reporting,
    IConfiguration configuration, ILogger<AppealDecisionReportingSweep> logger) : BackgroundService
{
    private const int BatchSize = 100;

    /// <summary>One pass over the cases due a try. Returns how many were accepted.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var accepted = 0;
        foreach (var decided in await store.ListUnreportedAppealDecisionsAsync(DateTime.UtcNow, BatchSize, ct))
        {
            if (await reporting.DeliverAsync(decided, ct) == AppealDecisionDelivery.Accepted) accepted++;
        }
        return accepted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("AppealReporting:SweepInterval", TimeSpan.FromMinutes(5));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Appeal-outcome reporting sweep failed; retrying next interval."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
