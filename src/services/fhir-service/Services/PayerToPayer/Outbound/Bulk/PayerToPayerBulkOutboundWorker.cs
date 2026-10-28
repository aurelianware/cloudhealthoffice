using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound.Bulk;

/// <summary>
/// Advances outbound bulk Payer-to-Payer jobs that are due: each pass claims
/// a few (so another instance does not advance the same job) and takes each
/// as far as it goes, in its own scope. A job that throws is let go and tried
/// again at its next attempt.
/// </summary>
public sealed class PayerToPayerBulkOutboundWorker : BackgroundService
{
    private const int ClaimBatch = 5;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPayerToPayerBulkOutboundJobStore _jobs;
    private readonly PayerToPayerBulkOutboundOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayerToPayerBulkOutboundWorker> _logger;

    public PayerToPayerBulkOutboundWorker(
        IServiceScopeFactory scopes,
        IPayerToPayerBulkOutboundJobStore jobs,
        IOptions<PayerToPayerBulkOutboundOptions> options,
        TimeProvider time,
        ILogger<PayerToPayerBulkOutboundWorker> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payer-to-Payer outbound bulk worker pass failed.");
            }

            // A full batch may mean more are waiting.
            if (claimed >= ClaimBatch) continue;
            try
            {
                await Task.Delay(_options.WorkerInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: claims due jobs and advances each. Returns how many were claimed.</summary>
    internal async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var jobs = await _jobs.ClaimDueAsync(now, now + _options.LeaseDuration, ClaimBatch, ct);
        foreach (var job in jobs)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IPayerToPayerBulkOutboundService>();
                await service.AdvanceAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Its lease runs out and it is claimed again at the next attempt.
                _logger.LogError(ex, "Payer-to-Payer outbound bulk {JobId} could not be advanced.", job.JobId);
            }
        }
        return jobs.Count;
    }
}
