using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// Polls each configured tenant's drop every <see cref="CpcdsDropOptions.Interval"/>
/// and loads what is ready, one scope (and one tenant) per run. Safe with
/// several replicas: a run owns only the files it moved out of inbound.
/// </summary>
public sealed class CpcdsDropWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CpcdsDropOptions _options;
    private readonly ILogger<CpcdsDropWorker> _logger;

    public CpcdsDropWorker(IServiceScopeFactory scopes, IOptions<CpcdsDropOptions> options, ILogger<CpcdsDropWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        // Tenants are read each tick, not once: a tenant that saves its drop
        // settings (the portal, onboarding) is picked up without a restart.
        _logger.LogInformation("CPCDS drop worker watching {Root} every {Interval}",
            LogSanitizer.Sanitize(_options.Root), _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval);
        do
        {
            IReadOnlyList<string> tenantIds;
            try
            {
                using var listScope = _scopes.CreateScope();
                tenantIds = await listScope.ServiceProvider.GetRequiredService<CpcdsDropTenants>().ListAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "CPCDS drop worker could not list tenants; retrying next tick");
                continue;
            }

            foreach (var tenantId in tenantIds)
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CpcdsDropRunner>().RunAsync(tenantId, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "CPCDS drop run failed for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
