using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// Runs an <see cref="IMessageSubscription"/> under the ASP.NET Core host
/// lifecycle. Consumers that want to subscribe from a long-running worker
/// register <c>AddHostedService&lt;SubscriptionHostedService&gt;()</c> with a
/// factory delegate that resolves the subscription from DI.
/// </summary>
public sealed class SubscriptionHostedService : BackgroundService
{
    private readonly Func<IServiceProvider, IMessageSubscription> _factory;
    private readonly IServiceProvider _services;
    private readonly ILogger<SubscriptionHostedService> _logger;
    private IMessageSubscription? _subscription;

    public SubscriptionHostedService(
        Func<IServiceProvider, IMessageSubscription> factory,
        IServiceProvider services,
        ILogger<SubscriptionHostedService> logger)
    {
        _factory = factory;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _factory(_services);
        _logger.LogInformation("Starting IMessageBus subscription");
        await _subscription.StartAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            try { await _subscription.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Subscription StopAsync threw"); }
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
