using CloudHealthOffice.Infrastructure.Gateways;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Responders;

internal sealed class InboundClaimAttachmentOutboxPublisher : BackgroundService
{
    private readonly CloudHealthOfficeClaimAttachmentReceiver _receiver;
    private readonly IOptions<HealthcareTransactionOptions> _options;
    private readonly ILogger<InboundClaimAttachmentOutboxPublisher> _logger;

    public InboundClaimAttachmentOutboxPublisher(
        CloudHealthOfficeClaimAttachmentReceiver receiver,
        IOptions<HealthcareTransactionOptions> options,
        ILogger<InboundClaimAttachmentOutboxPublisher> logger)
    {
        _receiver = receiver;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = _options.Value.ClaimLifecycle.OutboxIntervalSeconds;
        if (seconds <= 0)
        {
            _logger.LogInformation("Inbound claim attachment outbox publisher is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _receiver.DispatchPendingAsync(50, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound claim attachment outbox dispatch failed");
            }
        }
    }
}
