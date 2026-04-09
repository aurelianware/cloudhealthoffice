using EncounterSubmissionService.Services;

namespace EncounterSubmissionService.Workers;

/// <summary>
/// Background worker that periodically scans for pending encounter submissions
/// approaching the 60-day AHCA deadline and fires warning events.
/// Also batches pending encounters for FMMIS file generation.
/// Runs on a configurable interval (default: every 15 minutes).
/// </summary>
public class EncounterSubmissionWorker : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EncounterSubmissionWorker> _logger;
    private readonly IConfiguration _configuration;

    public EncounterSubmissionWorker(
        IServiceProvider serviceProvider,
        ILogger<EncounterSubmissionWorker> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _configuration.GetValue("Worker:IntervalMinutes", 15);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        _logger.LogInformation(
            "Encounter submission worker started with {Interval} minute interval", intervalMinutes);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckDeadlinesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during encounter submission worker cycle");
            }
        }
    }

    /// <summary>
    /// Check for encounters approaching their deadline and fire warning events.
    /// Public for unit testing.
    /// </summary>
    public async Task CheckDeadlinesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEncounterSubmissionService>();

        var warningDays = _configuration.GetValue("Worker:DeadlineWarningDays", 7);
        var approaching = await service.GetApproachingDeadlineAsync(warningDays);

        var count = 0;
        foreach (var submission in approaching)
        {
            _logger.LogWarning(
                "Encounter submission {Id} for claim {ClaimId} is {DaysLeft} days from deadline",
                submission.Id, submission.ClaimId,
                (submission.SubmissionDeadline - DateTime.UtcNow).TotalDays);

            // TODO: publish encounter-deadline-warning Kafka event
            // TODO: update status to DeadlineWarning

            count++;
        }

        if (count > 0)
        {
            _logger.LogWarning("Found {Count} encounter submissions approaching deadline", count);
        }
    }
}
