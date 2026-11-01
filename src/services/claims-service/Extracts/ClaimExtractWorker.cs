using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Extracts;

/// <summary>
/// Runs each enabled tenant's claims extract once a day, after
/// <see cref="ClaimExtractOptions.RunAtUtc"/>. Safe with several replicas:
/// a run leases the tenant, and a tenant already delivered today is skipped.
/// </summary>
public sealed class ClaimExtractWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ClaimExtractOptions _options;
    private readonly ILogger<ClaimExtractWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTime> _lastAttempt = new(StringComparer.Ordinal);

    public ClaimExtractWorker(
        IServiceScopeFactory scopes, IOptions<ClaimExtractOptions> options, ILogger<ClaimExtractWorker> logger, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        _logger.LogInformation("Claims extract worker: daily after {RunAt} UTC, checking every {Interval}",
            _options.RunAtUtc, _options.CheckInterval);
        using var timer = new PeriodicTimer(_options.CheckInterval);
        do
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Claims extract worker could not check tenants; retrying next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs every tenant whose extract is due now. Internal for tests.</summary>
    internal async Task RunDueAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var dueFrom = now.Date + _options.RunAtUtc;
        if (now < dueFrom)
            return;

        IReadOnlyList<ClaimExtractSettings> tenants;
        using (var listScope = _scopes.CreateScope())
            tenants = await listScope.ServiceProvider.GetRequiredService<IClaimExtractStore>().ListEnabledAsync(ct);

        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) return;
            if (tenant.LastSucceededAt >= dueFrom) continue;
            if (_lastAttempt.TryGetValue(tenant.TenantId, out var tried) && now - tried < _options.RetryAfter) continue;

            _lastAttempt[tenant.TenantId] = now;
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ClaimExtractRunner>()
                    .RunAsync(tenant.TenantId, ClaimExtractRunner.ScheduleActor, full: false, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Claims extract failed for tenant {TenantId}", LogSanitizer.Sanitize(tenant.TenantId));
            }
        }
    }
}
