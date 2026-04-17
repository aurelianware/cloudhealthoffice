using Microsoft.Extensions.Hosting;

namespace EligibilityService.Services;

/// <summary>
/// Background worker that drains the batch-eligibility queue and drives each
/// queued job through <see cref="IBatchEligibilityService"/>. Delegates the
/// actual queue-read loop to an <see cref="IBatchQueueProcessor"/> so the
/// worker is backend-agnostic: in-process channel in dev, Service Bus in
/// production.
/// </summary>
public class BatchEligibilityQueueWorker : BackgroundService
{
    private readonly IBatchQueueProcessor _processor;
    private readonly IServiceProvider _services;
    private readonly ILogger<BatchEligibilityQueueWorker> _logger;

    public BatchEligibilityQueueWorker(
        IBatchQueueProcessor processor,
        IServiceProvider services,
        ILogger<BatchEligibilityQueueWorker> logger)
    {
        _processor = processor;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BatchEligibilityQueueWorker started ({Processor})",
            _processor.GetType().Name);

        await _processor.RunAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(BatchQueueMessage msg, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IBatchEligibilityService>();
            await svc.ProcessJobAsync(msg.TenantId, msg.JobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process batch eligibility job {JobId} for tenant {Tenant}",
                msg.JobId, msg.TenantId);
            throw; // let the processor decide abandon / DLQ
        }

        // Opportunistic eviction: only meaningful for the in-memory store.
        // No-op for Cosmos (TTL handles expiration).
        if (_services.GetService<IBatchJobStore>() is InMemoryBatchJobStore memStore)
        {
            var evicted = memStore.Evict();
            if (evicted > 0)
                _logger.LogDebug("Evicted {Count} completed batch jobs", evicted);
        }
    }
}
