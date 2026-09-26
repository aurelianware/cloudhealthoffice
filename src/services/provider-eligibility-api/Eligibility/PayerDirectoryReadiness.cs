using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ProviderEligibilityApi.Eligibility;

/// <summary>
/// Whether this replica's payer directory has loaded. The directory is an
/// in-memory store filled by the Stedi sync, so until the first sync succeeds
/// every payer lookup misses. Without this gate those misses surface as
/// <c>PayerNotFound</c> (422, "fix your request") when the real problem is on
/// our side and the caller should retry.
///
/// Once ready, stays ready: later periodic syncs refresh a populated store,
/// and a failed refresh leaves the previous data in place.
/// </summary>
public sealed class PayerDirectoryReadiness
{
    private readonly IOptions<PayerReferenceOptions> _options;
    private readonly IServiceProvider _services;
    private volatile bool _ready;

    public PayerDirectoryReadiness(IOptions<PayerReferenceOptions> options, IServiceProvider services)
    {
        _options = options;
        _services = services;
    }

    public async ValueTask<bool> IsReadyAsync(CancellationToken ct = default)
    {
        if (_ready) return true;

        // Sync disabled (development with synthetic payers, tests): nothing to wait for.
        if (!_options.Value.Sync.Enabled)
        {
            _ready = true;
            return true;
        }

        var synchronizer = _services.GetService<IPayerDirectorySynchronizer>();
        if (synchronizer is null)
        {
            _ready = true;
            return true;
        }

        var status = await synchronizer.GetStatusAsync(ct).ConfigureAwait(false);
        if (status?.LastSucceededAt is not null)
        {
            _ready = true;
        }

        return _ready;
    }
}

/// <summary>Readiness probe: not ready until the payer directory has loaded.</summary>
public sealed class PayerDirectoryHealthCheck : IHealthCheck
{
    private readonly PayerDirectoryReadiness _readiness;

    public PayerDirectoryHealthCheck(PayerDirectoryReadiness readiness) => _readiness = readiness;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await _readiness.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("Payer directory loaded.")
            : HealthCheckResult.Unhealthy("Payer directory has not loaded yet.");
    }
}

/// <summary>
/// Retries the payer directory sync until it first succeeds. The shared sync
/// service makes one startup attempt and then waits a full interval (24h by
/// default); if that one attempt fails, this replica would otherwise stay
/// empty — and not ready — for a day. Exits once the directory is loaded.
/// </summary>
public sealed class PayerDirectoryStartupRetryService : BackgroundService
{
    // Leaves the shared service's startup attempt time to finish first, so the
    // two do not normally fetch the directory at the same time.
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    private readonly PayerDirectoryReadiness _readiness;
    private readonly IServiceProvider _services;
    private readonly IOptions<PayerReferenceOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayerDirectoryStartupRetryService> _logger;

    public PayerDirectoryStartupRetryService(
        PayerDirectoryReadiness readiness,
        IServiceProvider services,
        IOptions<PayerReferenceOptions> options,
        TimeProvider time,
        ILogger<PayerDirectoryStartupRetryService> logger)
    {
        _readiness = readiness;
        _services = services;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Sync.Enabled) return;
        var synchronizer = _services.GetService<IPayerDirectorySynchronizer>();
        if (synchronizer is null) return;

        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            if (await _readiness.IsReadyAsync(stoppingToken).ConfigureAwait(false)) return;

            _logger.LogWarning("Payer directory not loaded yet; retrying sync");
            try
            {
                var result = await synchronizer.SynchronizeAsync(stoppingToken).ConfigureAwait(false);
                if (result.Succeeded && await _readiness.IsReadyAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Payer directory loaded after retry");
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payer directory sync retry failed");
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }
}
