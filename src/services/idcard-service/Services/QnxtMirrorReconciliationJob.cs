using IdCardService.Models;
using IdCardService.Repositories;

namespace IdCardService.Services;

/// <summary>
/// Nightly backstop: scans recently-issued <see cref="IdCardRecord"/>s with
/// platform "qnxt" and re-enqueues any whose mirror message is missing. This
/// is the fallback designed into the augment-mode adapter so a transient
/// Service Bus outage during issuance doesn't silently drop requests.
///
/// For Phase 1 this is a stub that logs a heartbeat and exposes the job
/// surface area via DI so the Service Bus-backed implementation can drop in
/// without wiring changes.
/// </summary>
public class QnxtMirrorReconciliationJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QnxtMirrorReconciliationJob> _logger;

    public QnxtMirrorReconciliationJob(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<QnxtMirrorReconciliationJob> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = _configuration.GetValue<double>("IdCard:Reconciliation:IntervalHours", 24);
        var interval = TimeSpan.FromHours(Math.Max(1, intervalHours));

        // Stagger first run so startup isn't saturated.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

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
                _logger.LogError(ex, "QNXT mirror reconciliation pass failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var records = scope.ServiceProvider.GetRequiredService<IIdCardRecordRepository>();
        var queue = scope.ServiceProvider.GetRequiredService<IQnxtMirrorQueue>();

        // Look back one interval + a safety margin to catch anything issued
        // near the boundary of the previous run.
        var intervalHours = _configuration.GetValue<double>("IdCard:Reconciliation:IntervalHours", 24);
        var since = DateTime.UtcNow - TimeSpan.FromHours(intervalHours + 6);

        var recent = await records.ListIssuedSinceAsync(since, ct);
        // Filter to QNXT-issued, non-revoked records. Other platforms don't
        // have a mirror queue to reconcile with.
        var candidates = recent
            .Where(r => r.RevokedAt == null
                && string.Equals(r.Platform, "qnxt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var replayed = 0;
        foreach (var record in candidates)
        {
            try
            {
                await queue.EnqueueMirrorAsync(new QnxtMirrorMessage
                {
                    TenantId = record.TenantId,
                    MemberId = record.MemberId,
                    OrderId = record.OrderId,
                    CardId = record.CardId,
                    DocumentId = record.DocumentId,
                    IssuedAt = record.IssuedAt
                }, ct);
                replayed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-enqueue QNXT mirror for card {CardId}", record.CardId);
            }
        }

        _logger.LogInformation(
            "QNXT mirror reconciliation pass complete: examined={Examined} replayed={Replayed} since={Since:O}",
            candidates.Count, replayed, since);
    }
}
