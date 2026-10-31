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
        if (!_options.Enabled || _options.Tenants.Count == 0)
            return;

        _logger.LogInformation("CPCDS drop worker watching {Count} tenants under {Root} every {Interval}",
            _options.Tenants.Count, LogSanitizer.Sanitize(_options.Root), _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval);
        do
        {
            foreach (var tenantId in _options.Tenants.Keys)
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
