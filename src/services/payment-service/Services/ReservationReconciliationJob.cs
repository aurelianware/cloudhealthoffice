using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// Hosted job that reconciles stranded claim reservations
/// (<see cref="IReservationReconciliationService"/>) every
/// <c>PaymentRuns:ReservationReconciliationInterval</c> (default 5 minutes; the
/// first pass waits one interval). It runs as payment-service itself: it reads
/// and changes payment-service's own state only and makes no claims-service or
/// other outbound call, so it needs no token. It works one tenant at a time,
/// each in its own DI scope whose request context names that tenant, so every
/// tenant-scoped repository sees only that tenant's runs, payments and 835s.
/// <c>PaymentRuns:ReservationReconciliationEnabled=false</c> turns it off.
/// </summary>
public sealed class ReservationReconciliationJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ReservationReconciliationOptions> _options;
    private readonly ILogger<ReservationReconciliationJob> _logger;

    public ReservationReconciliationJob(
        IServiceProvider services,
        IOptions<ReservationReconciliationOptions> options,
        ILogger<ReservationReconciliationJob> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.ReservationReconciliationEnabled)
        {
            _logger.LogInformation("Claim reservation reconciliation is disabled");
            return;
        }

        var interval = options.ReservationReconciliationInterval > TimeSpan.Zero
            ? options.ReservationReconciliationInterval
            : TimeSpan.FromMinutes(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }

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
                _logger.LogError(ex, "Claim reservation reconciliation pass failed");
            }
        }
    }

    /// <summary>One pass over every tenant that holds reservations. A failing tenant does not stop the others.</summary>
    public async Task<IReadOnlyList<ReconciliationPassResult>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<string> tenants;
        using (var scope = _services.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IClaimReservationRepository>().ListTenantsAsync();
        }

        var results = new List<ReconciliationPassResult>();
        foreach (var tenantId in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ReconcileTenantAsync(tenantId, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claim reservation reconciliation failed for tenant {TenantId}",
                    tenantId.Replace("\r", string.Empty).Replace("\n", string.Empty));
            }
        }
        return results;
    }

    private async Task<ReconciliationPassResult> ReconcileTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();

        // The repositories take the tenant from the request context; this pass
        // has no request, so it gets a context naming only this tenant (and no
        // user: nothing here acts for a person or calls another service).
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Items["TenantId"] = tenantId;
        accessor.HttpContext = context;
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<IReservationReconciliationService>();
            return await service.ReconcileCurrentTenantAsync(cancellationToken);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
