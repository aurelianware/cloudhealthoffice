using AuthorizationService.Models;
using AuthorizationService.Repositories;

namespace AuthorizationService.Services;

/// <summary>
/// Background service that periodically evaluates open authorizations
/// and escalates SLA levels as deadlines approach or are breached.
/// </summary>
public class SlaWatchdogService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // Urgent (LevelOfService == "U") thresholds in hours
    private const double UrgentWarningHours = 48;
    private const double UrgentCriticalHours = 64;
    private const double UrgentBreachHours = 72;

    // Standard (everything else) thresholds in hours
    private const double StandardWarningHours = 120;
    private const double StandardCriticalHours = 144;
    private const double StandardBreachHours = 168;

    private readonly IAuthorizationRepository _repository;
    private readonly ILogger<SlaWatchdogService> _logger;

    public SlaWatchdogService(
        IServiceProvider serviceProvider,
        ILogger<SlaWatchdogService> logger)
    {
        var scope = serviceProvider.CreateScope();
        _repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAllAuthorizationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA watchdog evaluation failed");
            }
        }
    }

    /// <summary>
    /// Evaluates all open authorizations and escalates SLA levels.
    /// Public to allow direct invocation from tests.
    /// </summary>
    public async Task EvaluateAllAuthorizationsAsync()
    {
        var auths = await _repository.GetOpenAuthorizationsAsync();

        foreach (var auth in auths)
        {
            if (!IsOpenStatus(auth.Status))
                continue;

            var slaStart = auth.SlaResumedAt ?? auth.SubmittedDate;
            var elapsed = (DateTime.UtcNow - slaStart).TotalHours;
            var isUrgent = auth.LevelOfService == "U";
            var deadline = isUrgent ? UrgentBreachHours : StandardBreachHours;
            var remaining = deadline - elapsed;

            var newLevel = ComputeEscalationLevel(elapsed, isUrgent);

            if (newLevel > auth.SlaEscalation)
            {
                auth.SlaEscalation = newLevel;
                auth.SlaEscalatedAt = DateTime.UtcNow;
                await _repository.UpdateAsync(auth);

                _logger.LogWarning(
                    "SLA {Level} for authorization {AuthNumber}: {HoursElapsed:F1}h elapsed, {HoursRemaining:F1}h remaining",
                    newLevel, auth.AuthorizationNumber, elapsed, remaining);
            }
        }
    }

    /// <summary>
    /// Computes the SLA status DTO for a single authorization (used by the endpoint).
    /// </summary>
    public static AuthorizationSlaStatus ComputeSlaStatus(Authorization auth)
    {
        var slaStart = auth.SlaResumedAt ?? auth.SubmittedDate;
        var isUrgent = auth.LevelOfService == "U";
        var deadline = isUrgent ? UrgentBreachHours : StandardBreachHours;
        var elapsed = (DateTime.UtcNow - slaStart).TotalHours;
        var remaining = deadline - elapsed;
        var percentConsumed = deadline > 0 ? (elapsed / deadline) * 100 : 100;
        var escalation = ComputeEscalationLevel(elapsed, isUrgent);

        return new AuthorizationSlaStatus
        {
            Id = auth.Id,
            AuthorizationNumber = auth.AuthorizationNumber,
            MemberId = auth.MemberId,
            TenantId = auth.TenantId,
            Status = auth.Status,
            LevelOfService = auth.LevelOfService,
            SlaStartedAt = slaStart,
            SlaDeadline = slaStart.AddHours(deadline),
            HoursElapsed = Math.Round(elapsed, 1),
            HoursRemaining = Math.Round(remaining, 1),
            PercentConsumed = Math.Round(percentConsumed, 1),
            EscalationLevel = escalation,
        };
    }

    private static SlaEscalationLevel ComputeEscalationLevel(double hoursElapsed, bool isUrgent)
    {
        var (warning, critical, breach) = isUrgent
            ? (UrgentWarningHours, UrgentCriticalHours, UrgentBreachHours)
            : (StandardWarningHours, StandardCriticalHours, StandardBreachHours);

        if (hoursElapsed >= breach) return SlaEscalationLevel.Breach;
        if (hoursElapsed >= critical) return SlaEscalationLevel.Critical;
        if (hoursElapsed >= warning) return SlaEscalationLevel.Warning;
        return SlaEscalationLevel.None;
    }

    private static bool IsOpenStatus(AuthorizationStatus status) =>
        status is AuthorizationStatus.Submitted
            or AuthorizationStatus.InReview
            or AuthorizationStatus.Pended;
}
