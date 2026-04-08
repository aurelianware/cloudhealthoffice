using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Lifecycle;

/// <summary>
/// Revalidation Alert Engine — scans the enrollment cache for providers whose
/// Medicaid revalidation deadline falls within the configured warning window
/// and raises RevalidationDueEvents for each.
///
/// Execution model:
///   KEDA CronJob — runs daily at 6:00 AM CT, separate from the batch sync.
///
///   KEDA schedule:
///     triggers:
///       - type: cron
///         metadata:
///           timezone: "America/Chicago"
///           start: "0 6 * * *"
///           end:   "0 7 * * *"
///
/// Notification flow:
///   RevalidationAlertEngine → IEnrollmentNotificationHandler
///     → (host-provided) → Azure Service Bus / SendGrid / portal alert table
///
/// Default warning window: 90 days (ProviderEnrollmentOptions.RevalidationWarningDays).
/// Alerts are raised every day the provider remains within the window —
/// callers are responsible for deduplication if only one alert per provider is wanted.
/// </summary>
public sealed class RevalidationAlertEngine : IHostedService
{
    private readonly IEnrollmentRepository _repository;
    private readonly IEnrollmentNotificationHandler _notifications;
    private readonly ProviderEnrollmentOptions _opts;
    private readonly ILogger<RevalidationAlertEngine> _logger;

    public RevalidationAlertEngine(
        IEnrollmentRepository repository,
        IEnrollmentNotificationHandler notifications,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<RevalidationAlertEngine> logger)
    {
        _repository    = repository;
        _notifications = notifications;
        _opts          = options.Value;
        _logger        = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "RevalidationAlertEngine started — scanning for revalidations due within {Days} days",
            _opts.RevalidationWarningDays);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Scan all enabled states (or all states if not filtered)
        var statesToScan = _opts.EnabledStateCodes.Count > 0
            ? _opts.EnabledStateCodes
            : new List<string> { null! };  // null = all states

        var allAtRisk = new List<Models.StateEnrollmentRecord>();

        foreach (var stateCode in statesToScan)
        {
            ct.ThrowIfCancellationRequested();
            var records = await _repository.GetProvidersWithRevalidationDueSoonAsync(
                _opts.RevalidationWarningDays,
                stateCode == null! ? null : stateCode,
                ct);

            allAtRisk.AddRange(records);
        }

        _logger.LogInformation(
            "Found {Count} providers with revalidation due within {Days} days",
            allAtRisk.Count, _opts.RevalidationWarningDays);

        // Raise an event for each at-risk provider
        var tasks = allAtRisk.Select(async record =>
        {
            var daysRemaining = record.RevalidationDueDate!.Value.DayNumber - today.DayNumber;

            var evt = new RevalidationDueEvent
            {
                Npi                 = record.Npi,
                StateCode           = record.StateCode,
                SourceSystem        = record.SourceSystem,
                RevalidationDueDate = record.RevalidationDueDate!.Value,
                DaysRemaining       = daysRemaining
            };

            try
            {
                await _notifications.HandleRevalidationDueAsync(evt, ct);

                _logger.LogInformation(
                    "Revalidation alert raised: NPI={Npi} State={State} DueDate={Due} DaysRemaining={Days}",
                    record.Npi, record.StateCode, record.RevalidationDueDate!.Value, daysRemaining);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to raise revalidation alert for NPI={Npi} State={State}",
                    record.Npi, record.StateCode);
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("RevalidationAlertEngine complete — {Count} alerts raised", allAtRisk.Count);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("RevalidationAlertEngine stopping");
        return Task.CompletedTask;
    }
}
