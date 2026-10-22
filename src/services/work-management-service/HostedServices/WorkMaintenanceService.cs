using Microsoft.Extensions.Options;
using WorkManagementService.Repositories;
using WorkManagementService.Services;

namespace WorkManagementService.HostedServices;

/// <summary>
/// Background upkeep, per tenant this service knows (any tenant with queues
/// or items, plus <c>WorkManagement:Reconcile:Tenants</c>):
/// <list type="bullet">
///   <item><b>Reconcile</b> against claims-service every
///   <see cref="ReconcileOptions.IntervalMinutes"/>, when enabled. Automatic
///   default: enabled exactly when this service has no Kafka (so it cannot
///   receive claims.work.v1, e.g. the GKE / local demo whose message bus is
///   in-memory and process-local), disabled when events flow.</item>
///   <item><b>Re-score</b> open items every
///   <see cref="WorkManagementOptions.RescoreIntervalMinutes"/>: priority is
///   deadline- and age-driven, so it rises while an item waits.</item>
/// </list>
/// Each tenant runs in its own scope; a failure is logged and the next tick retries.
/// </summary>
public sealed class WorkMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly WorkManagementOptions _options;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkMaintenanceService> _logger;

    public WorkMaintenanceService(
        IServiceProvider services, IOptions<WorkManagementOptions> options, IConfiguration configuration,
        TimeProvider time, ILogger<WorkMaintenanceService> logger)
    {
        _services = services;
        _options = options.Value;
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>The effective reconcile switch (explicit setting, else on exactly when there is no Kafka).</summary>
    public static bool ReconcileEnabled(WorkManagementOptions options, IConfiguration configuration) =>
        options.Reconcile.Enabled ?? string.IsNullOrWhiteSpace(configuration["Kafka:BootstrapServers"]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconcile = ReconcileEnabled(_options, _configuration);
        var enrollment = _options.EnrollmentReconcile.Enabled;
        var rescore = _options.RescoreIntervalMinutes > 0;
        _logger.LogInformation("Work maintenance: periodic reconcile {Reconcile} (every {Interval} min), re-score {Rescore}",
            reconcile ? "on" : "off", _options.Reconcile.IntervalMinutes, rescore ? $"every {_options.RescoreIntervalMinutes} min" : "off");
        if (!reconcile && !rescore && !enrollment) return;

        var lastReconcile = DateTimeOffset.MinValue;
        var lastEnrollment = DateTimeOffset.MinValue;
        var lastRescore = _time.GetUtcNow();
        // A short first delay lets the host (and claims-service) come up.
        try { await Task.Delay(TimeSpan.FromSeconds(20), _time, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var doReconcile = reconcile && now - lastReconcile >= TimeSpan.FromMinutes(Math.Max(1, _options.Reconcile.IntervalMinutes));
            var doRescore = rescore && now - lastRescore >= TimeSpan.FromMinutes(_options.RescoreIntervalMinutes);
            var doEnrollment = enrollment && now - lastEnrollment >= TimeSpan.FromMinutes(Math.Max(1, _options.EnrollmentReconcile.IntervalMinutes));
            if (doReconcile || doRescore || doEnrollment)
            {
                await RunOnceAsync(doReconcile, doRescore, stoppingToken, doEnrollment);
                if (doReconcile) lastReconcile = now;
                if (doRescore) lastRescore = now;
                if (doEnrollment) lastEnrollment = now;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), _time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every known tenant (internal for tests).</summary>
    internal async Task RunOnceAsync(bool reconcile, bool rescore, CancellationToken ct, bool enrollment = false)
    {
        IReadOnlyList<string> tenants;
        using (var scope = _services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkStore>();
            tenants = (await store.ListTenantsAsync(ct)).Concat(_options.Reconcile.Tenants)
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var tenant in tenants)
        {
            try
            {
                using var scope = _services.CreateScope();
                var intake = scope.ServiceProvider.GetRequiredService<WorkIntakeService>();
                if (reconcile)
                {
                    var source = scope.ServiceProvider.GetRequiredService<IClaimsWorkSource>();
                    await intake.ReconcileClaimsAsync(tenant, source, Math.Clamp(_options.Reconcile.PageSize, 1, 1000), ct);
                }
                if (enrollment)
                {
                    var source = scope.ServiceProvider.GetRequiredService<IEnrollmentWorkSource>();
                    await intake.ReconcileEnrollmentAsync(tenant, source, Math.Clamp(_options.EnrollmentReconcile.PageSize, 1, 1000), ct);
                }
                if (rescore)
                    await intake.RecomputeAsync(tenant, reroute: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Work maintenance failed for tenant {TenantId}; retried next tick", tenant);
            }
        }
    }
}
