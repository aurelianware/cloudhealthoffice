using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Optional poller for inbound 277CAs via Stedi Poll Transactions
/// (<c>GET https://core.us.stedi.com/2023-08-01/polling/transactions</c>).
/// Disabled by default. Webhook and poll share
/// <see cref="IClaimAcknowledgmentIngress"/>.
/// </summary>
internal sealed class StediClaimAcknowledgmentPoller : BackgroundService
{
    private readonly StediClaimAcknowledgmentApiClient _client;
    private readonly IClaimAcknowledgmentIngress _ingress;
    private readonly IClaimAcknowledgmentCursorStore _cursors;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediClaimAcknowledgmentPoller> _logger;
    private readonly TimeProvider _timeProvider;

    public StediClaimAcknowledgmentPoller(
        StediClaimAcknowledgmentApiClient client,
        IClaimAcknowledgmentIngress ingress,
        IClaimAcknowledgmentCursorStore cursors,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimAcknowledgmentPoller> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _ingress = ingress;
        _cursors = cursors;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.ClaimAcknowledgmentPollingEnabled)
        {
            _logger.LogInformation("Stedi 277CA polling is disabled.");
            return;
        }

        if (_options.Value.ClaimAcknowledgmentPollingOnStartup)
        {
            await RunOnce(stoppingToken).ConfigureAwait(false);
        }

        var seconds = _options.Value.ClaimAcknowledgmentPollingIntervalSeconds <= 0
            ? 60
            : _options.Value.ClaimAcknowledgmentPollingIntervalSeconds;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnce(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunOnce(CancellationToken ct)
    {
        try
        {
            var cursor = await _cursors.GetAsync(StediHealthcareGateway.GatewayName, ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var windowStart = cursor?.WindowStartUtc ?? now.AddDays(-1);
            var pageToken = cursor?.PageToken;
            var start = pageToken is null ? windowStart.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") : null;

            var page = await _client.PollAsync(start, pageToken, ct).ConfigureAwait(false);
            foreach (var item in page.Page.Items ?? Enumerable.Empty<Models.StediPollTransactionItemDto>())
            {
                if (!string.Equals(item.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var discovery = new ClaimAcknowledgmentDiscovery
                {
                    GatewayName = StediHealthcareGateway.GatewayName,
                    ExternalAcknowledgmentId = item.TransactionId ?? string.Empty,
                    Direction = item.Direction,
                    TransactionSetIdentifier =
                        item.X12?.Metadata?.Transaction?.TransactionSetIdentifier ??
                        item.X12?.TransactionSetIdentifier
                };

                var result = await _ingress.IngestDiscoveredAsync(discovery, ct).ConfigureAwait(false);
                if (result.TransientFailure)
                {
                    _logger.LogWarning(
                        "Transient 277CA poll ingest for transaction {TransactionId} category={Category}",
                        Sanitize(item.TransactionId), result.ErrorCategory);
                    return;
                }

                if (!result.Ignored && !result.Processed && !result.Replay)
                {
                    _logger.LogWarning(
                        "277CA poll ingest did not complete for transaction {TransactionId} category={Category}",
                        Sanitize(item.TransactionId), result.ErrorCategory);
                    return;
                }
            }

            var nextToken = page.Page.NextPageToken;
            var nextWindow = windowStart;
            if (string.IsNullOrWhiteSpace(nextToken))
            {
                nextToken = null;
                nextWindow = now.AddDays(-1);
            }

            await _cursors.SaveAsync(new ClaimAcknowledgmentCursor
            {
                GatewayName = StediHealthcareGateway.GatewayName,
                PageToken = nextToken,
                LastSuccessAtUtc = now,
                WindowStartUtc = nextWindow
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stedi 277CA poll cycle failed unexpectedly");
        }
    }

    private static string? Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
