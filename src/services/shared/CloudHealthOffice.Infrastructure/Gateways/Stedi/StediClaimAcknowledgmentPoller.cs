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
    private readonly IRemittanceIngress _remittanceIngress;
    private readonly IClaimAcknowledgmentCursorStore _cursors;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly IOptions<HealthcareTransactionOptions> _lifecycle;
    private readonly ILogger<StediClaimAcknowledgmentPoller> _logger;
    private readonly TimeProvider _timeProvider;

    public StediClaimAcknowledgmentPoller(
        StediClaimAcknowledgmentApiClient client,
        IClaimAcknowledgmentIngress ingress,
        IRemittanceIngress remittanceIngress,
        IClaimAcknowledgmentCursorStore cursors,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimAcknowledgmentPoller> logger,
        TimeProvider? timeProvider = null,
        IOptions<HealthcareTransactionOptions>? lifecycle = null)
    {
        _client = client;
        _ingress = ingress;
        _remittanceIngress = remittanceIngress;
        _cursors = cursors;
        _options = options;
        _lifecycle = lifecycle ?? Options.Create(new HealthcareTransactionOptions());
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

                if (RemittanceIngress.IsInbound835(discovery, out _))
                {
                    var remittance = await _remittanceIngress
                        .IngestDiscoveredAsync(discovery, ct).ConfigureAwait(false);
                    if (remittance.TransientFailure)
                    {
                        _logger.LogWarning(
                            "Transient 835 poll ingest for transaction {TransactionId} category={Category}",
                            Sanitize(item.TransactionId), remittance.ErrorCategory);
                        return;
                    }

                    continue;
                }

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
            DateTimeOffset? polledThrough = cursor?.LastPolledThroughUtc;
            if (string.IsNullOrWhiteSpace(nextToken))
            {
                nextToken = null;
                var overlapHours = _lifecycle.Value.ClaimLifecycle.PollOverlapHours;
                if (overlapHours <= 0)
                {
                    overlapHours = 24;
                }

                // Advance past this cycle while retaining Stedi's ≥1-day overlap.
                polledThrough = now;
                nextWindow = now.AddHours(-overlapHours);
            }

            await _cursors.SaveAsync(new ClaimAcknowledgmentCursor
            {
                GatewayName = StediHealthcareGateway.GatewayName,
                PageToken = nextToken,
                LastSuccessAtUtc = now,
                LastFailureAtUtc = cursor?.LastFailureAtUtc,
                WindowStartUtc = nextWindow,
                LastPolledThroughUtc = polledThrough
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stedi 277CA poll cycle failed unexpectedly");
            try
            {
                var failed = await _cursors.GetAsync(StediHealthcareGateway.GatewayName, ct)
                    .ConfigureAwait(false) ?? new ClaimAcknowledgmentCursor
                    {
                        GatewayName = StediHealthcareGateway.GatewayName
                    };
                failed.LastFailureAtUtc = _timeProvider.GetUtcNow();
                await _cursors.SaveAsync(failed, ct).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original poll failure; cursor persistence is best-effort.
            }
        }
    }

    private static string? Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
