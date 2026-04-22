using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Bridges the IConfiguration reload token fired by
/// <see cref="SecretProviderConfigurationProvider"/> to the in-process key
/// cache in <see cref="RotatingKeyProvider"/>. Every reload clears the
/// cache so the next sign/verify resolves the newest secret value.
/// </summary>
/// <remarks>
/// No debounce. <see cref="RotatingKeyProvider.InvalidateCache"/> is a
/// dictionary Clear plus a log line — microseconds. Dropping a genuine
/// rotation signal is worse than extra invalidations.
///
/// The callback is wrapped in a try/catch so an unexpected throw in
/// <see cref="RotatingKeyProvider.InvalidateCache"/> cannot silently tear
/// down the reload-token subscription — if that happened, rotation would
/// stop propagating without any log evidence.
/// </remarks>
public sealed class SecretRefreshService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly RotatingKeyProvider _keys;
    private readonly ILogger<SecretRefreshService> _logger;
    private IDisposable? _registration;

    public SecretRefreshService(
        IConfiguration configuration,
        RotatingKeyProvider keys,
        ILogger<SecretRefreshService> logger)
    {
        _configuration = configuration;
        _keys = keys;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registration = ChangeToken.OnChange(
            () => _configuration.GetReloadToken(),
            OnReload);

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void OnReload()
    {
        try
        {
            _keys.InvalidateCache();
        }
        catch (Exception ex)
        {
            // ChangeToken.OnChange re-subscribes for subsequent signals
            // independently of whether the callback threw, but we swallow
            // here for belt-and-braces: an exception bubbling out of this
            // callback could tear down the hosted service and negate the
            // whole rotation flow.
            _logger.LogError(ex,
                "RotatingKeyProvider.InvalidateCache threw during IConfiguration reload; " +
                "subsequent reloads will still be processed.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return base.StopAsync(cancellationToken);
    }
}
