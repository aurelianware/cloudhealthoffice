using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Adjudicates queued background shadow runs (a whole import batch each),
/// one at a time per replica. A run is taken with a lease that is renewed
/// after each page; a run whose replica stopped is taken over once its lease
/// passes and resumes after the claims it already has. Off unless
/// <c>Adjudication:Shadow:Enabled</c>.
/// </summary>
public sealed class ShadowAdjudicationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ShadowAdjudicationOptions _options;
    private readonly ILogger<ShadowAdjudicationWorker> _logger;

    public ShadowAdjudicationWorker(
        IServiceScopeFactory scopes, IOptions<ShadowAdjudicationOptions> options, ILogger<ShadowAdjudicationWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        using var timer = new PeriodicTimer(_options.WorkerInterval > TimeSpan.Zero ? _options.WorkerInterval : TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && await RunNextAsync(stoppingToken))
                {
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Shadow adjudication worker could not take a run; retrying next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Takes and adjudicates one queued run. False when none was waiting.</summary>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShadowAdjudicationRepository>();
        var now = DateTime.UtcNow;
        var run = await store.TakeNextBackgroundRunAsync(now, now + _options.LeaseDuration, ct);
        if (run is null)
            return false;

        try
        {
            await scope.ServiceProvider.GetRequiredService<IShadowAdjudicationService>().ProcessBackgroundRunAsync(run, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease lapses and another replica (or this one, restarted) resumes the run.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shadow adjudication background run {RunId} failed", LogSanitizer.Sanitize(run.Id));
            run.Status = ShadowRunStatus.Failed;
            run.Error = $"The run stopped: {ex.GetType().Name}. Results stored so far are kept.";
            run.LeaseUntilUtc = null;
            run.CompletedAtUtc = DateTime.UtcNow;
            await store.SaveRunStateAsync(run, CancellationToken.None);
        }
        return true;
    }
}
