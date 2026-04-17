using Microsoft.Extensions.Hosting;

namespace EligibilityService.Services;

/// <summary>
/// Background worker that drains IBatchQueue and drives each queued job
/// through IBatchEligibilityService. One instance per host; multiple hosts
/// coordinate via the queue semantics (Service Bus in production).
/// </summary>
public class BatchEligibilityQueueWorker : BackgroundService
{
    private readonly IBatchQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<BatchEligibilityQueueWorker> _logger;

    public BatchEligibilityQueueWorker(
        IBatchQueue queue,
        IServiceProvider services,
        ILogger<BatchEligibilityQueueWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BatchEligibilityQueueWorker started");

        await foreach (var msg in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IBatchEligibilityService>();
                await svc.ProcessJobAsync(msg.TenantId, msg.JobId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process batch eligibility job {JobId} for tenant {Tenant}",
                    msg.JobId, msg.TenantId);
            }
        }
    }
}
