using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Optional periodic Stedi payer-directory refresh. Disabled by default so
/// hosts start without live vendor credentials. Failures are logged and do
/// not stop the application.
/// </summary>
internal sealed class PayerDirectorySyncHostedService : BackgroundService
{
    private readonly IPayerDirectorySynchronizer _synchronizer;
    private readonly IOptions<PayerReferenceOptions> _options;
    private readonly ILogger<PayerDirectorySyncHostedService> _logger;

    public PayerDirectorySyncHostedService(
        IPayerDirectorySynchronizer synchronizer,
        IOptions<PayerReferenceOptions> options,
        ILogger<PayerDirectorySyncHostedService> logger)
    {
        _synchronizer = synchronizer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sync = _options.Value.Sync;
        if (!sync.Enabled)
        {
            _logger.LogInformation("Payer directory periodic sync is disabled.");
            return;
        }

        if (sync.OnStartup)
        {
            await RunOnce(stoppingToken).ConfigureAwait(false);
        }

        var hours = sync.IntervalHours <= 0 ? 24 : sync.IntervalHours;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnce(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        try
        {
            var result = await _synchronizer.SynchronizeAsync(ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Scheduled payer directory sync did not succeed: {Error}", result.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled payer directory sync failed unexpectedly");
        }
    }
}
