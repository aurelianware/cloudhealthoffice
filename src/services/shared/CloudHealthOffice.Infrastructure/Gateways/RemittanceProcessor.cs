using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Applies a canonical 835 to durable remittance storage. Tenant identity
/// always comes from matched transmissions. Duplicate deliveries are no-ops.
/// Does not post payment or mutate 277CA / 276/277 / transmission status.
/// </summary>
public sealed class RemittanceProcessor : IRemittanceProcessor
{
    private readonly IRemittanceStore _receipts;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IMessageBus? _messageBus;
    private readonly ILogger<RemittanceProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public RemittanceProcessor(
        IRemittanceStore receipts,
        IClaimTransmissionStore transmissions,
        ILogger<RemittanceProcessor> logger,
        IMessageBus? messageBus = null,
        TimeProvider? timeProvider = null)
    {
        _receipts = receipts;
        _transmissions = transmissions;
        _logger = logger;
        _messageBus = messageBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RemittanceProcessResult> ProcessAsync(
        GatewayRemittance remittance,
        CancellationToken cancellationToken = default)
    {
        var started = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(remittance.Gateway))
        {
            remittance.Gateway = "unknown";
        }

        if (string.IsNullOrWhiteSpace(remittance.RemittanceId))
        {
            remittance.RemittanceId =
                remittance.ExternalTransactionId ??
                remittance.EventId ??
                Guid.NewGuid().ToString("N");
        }

        if (!string.IsNullOrWhiteSpace(remittance.EventId))
        {
            var byEvent = await _receipts
                .GetByEventIdAsync(remittance.Gateway, remittance.EventId, cancellationToken)
                .ConfigureAwait(false);
            if (byEvent is not null)
            {
                return await ReplayAsync(byEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        var existing = await _receipts
            .GetByIdempotencyKeyAsync(remittance.Gateway, remittance.RemittanceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await ReplayAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var receipt = await BuildReceiptAsync(remittance, started, cancellationToken).ConfigureAwait(false);
        return await CommitNewAsync(receipt, started, cancellationToken).ConfigureAwait(false);
    }

    public async Task DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _receipts.ListPendingOutboxAsync(50, cancellationToken).ConfigureAwait(false);
        foreach (var record in pending)
        {
            await ReplayAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RemittanceReceipt> BuildReceiptAsync(
        GatewayRemittance remittance,
        DateTimeOffset started,
        CancellationToken ct)
    {
        var receipt = ToRecord(remittance);
        var tenants = new HashSet<string>(StringComparer.Ordinal);
        var matched = 0;
        var ambiguous = 0;

        foreach (var claim in receipt.Claims)
        {
            var match = await RemittanceMatcher
                .MatchClaimAsync(claim, remittance.Gateway, remittance.TransmissionId, _transmissions, ct)
                .ConfigureAwait(false);
            if (match.Ambiguous)
            {
                claim.MatchStatus = RemittanceClaimMatchStatus.Ambiguous;
                claim.MatchReason = match.Reason;
                ambiguous++;
                continue;
            }

            if (match.Transmission is null)
            {
                claim.MatchStatus = RemittanceClaimMatchStatus.Unmatched;
                claim.MatchReason = match.Reason ?? "unmatched";
                continue;
            }

            claim.MatchStatus = RemittanceClaimMatchStatus.Matched;
            claim.MatchReason = match.Reason;
            claim.TransmissionId = match.Transmission.TransmissionId;
            claim.ClaimId = match.Transmission.ClaimId;
            tenants.Add(match.Transmission.TenantId);
            matched++;
        }

        if (tenants.Count > 1)
        {
            receipt.Status = RemittanceLifecycleStatus.Failed;
            receipt.TenantId = string.Empty;
            receipt.UnmatchedReason = "mixed-tenant";
            receipt.LastErrorCategory = GatewayErrorCategory.AmbiguousClaim;
            receipt.LastError = "mixed-tenant";
            foreach (var claim in receipt.Claims)
            {
                claim.MatchStatus = RemittanceClaimMatchStatus.Ambiguous;
                claim.MatchReason = "mixed-tenant";
                claim.TransmissionId = null;
            }

            return receipt;
        }

        if (ambiguous > 0 && matched == 0)
        {
            receipt.Status = RemittanceLifecycleStatus.Unmatched;
            receipt.UnmatchedReason = "ambiguous-claim";
            receipt.LastErrorCategory = GatewayErrorCategory.AmbiguousClaim;
            receipt.LastError = "ambiguous-claim";
            return receipt;
        }

        if (receipt.Claims.Count == 0)
        {
            receipt.Status = RemittanceLifecycleStatus.Failed;
            receipt.UnmatchedReason = "malformed";
            receipt.LastErrorCategory = GatewayErrorCategory.MalformedResponse;
            receipt.LastError = "malformed-835";
            return receipt;
        }

        if (matched == 0)
        {
            receipt.Status = RemittanceLifecycleStatus.Unmatched;
            receipt.UnmatchedReason = "no-deterministic-identifier";
            receipt.LastErrorCategory = GatewayErrorCategory.UnableToMatch;
            receipt.LastError = receipt.UnmatchedReason;
            return receipt;
        }

        receipt.TenantId = tenants.First();
        receipt.PayerId ??= remittance.PayerIdentifier;
        receipt.Status = RemittanceLifecycleStatus.AvailableForPosting;
        return receipt;
    }

    private async Task<RemittanceProcessResult> CommitNewAsync(
        RemittanceReceipt record,
        DateTimeOffset started,
        CancellationToken ct)
    {
        EnsureOutbox(record, started);
        record.ProcessingAttempts = 1;
        var (created, stored) = await _receipts.TryCreateAsync(record, ct).ConfigureAwait(false);
        if (!created)
        {
            return await ReplayAsync(stored, ct).ConfigureAwait(false);
        }

        var published = await PublishPendingAsync(stored, ct).ConfigureAwait(false);
        await _receipts.SaveAsync(stored, ct).ConfigureAwait(false);
        Log(stored, replay: false);
        RecordMetric(stored, _timeProvider.GetUtcNow() - started);
        return ToResult(stored, replay: false, events: published);
    }

    private async Task<RemittanceProcessResult> ReplayAsync(
        RemittanceReceipt existing, CancellationToken ct)
    {
        existing.ProcessingAttempts++;
        var published = await PublishPendingAsync(existing, ct).ConfigureAwait(false);
        await _receipts.SaveAsync(existing, ct).ConfigureAwait(false);
        Log(existing, replay: true);
        return ToResult(existing, replay: true, events: published);
    }

    private static RemittanceReceipt ToRecord(GatewayRemittance remittance) =>
        new()
        {
            RemittanceId = remittance.RemittanceId,
            Gateway = remittance.Gateway,
            EventId = remittance.EventId,
            ExternalTransactionId = remittance.ExternalTransactionId,
            PaymentIdentifier = remittance.PaymentIdentifier,
            PaymentMethodCode = remittance.PaymentMethodCode,
            PaymentDate = remittance.PaymentDate,
            PaymentAmount = remittance.PaymentAmount,
            ReceivedAtUtc = remittance.ReceivedAt == default
                ? DateTimeOffset.UtcNow
                : remittance.ReceivedAt,
            CorrelationId = remittance.CorrelationId,
            RawSourceReference = remittance.RawSourceReference,
            Claims = remittance.Claims.ToList()
        };

    private static RemittanceProcessResult ToResult(
        RemittanceReceipt record, bool replay, bool events) =>
        new()
        {
            Replay = replay,
            Status = record.Status,
            RemittanceId = record.RemittanceId,
            TenantId = record.TenantId,
            ClaimCount = record.Claims.Count,
            MatchedClaimCount = record.Claims.Count(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched),
            ErrorCategory = record.LastErrorCategory,
            ErrorMessage = record.LastError,
            EventsPublished = events && !replay
        };

    private static void EnsureOutbox(RemittanceReceipt record, DateTimeOffset now)
    {
        if (record.Outbox.Count > 0)
        {
            return;
        }

        record.Outbox.Add(Entry(RemittanceMessageTypes.Received, now));
        if (record.Status is RemittanceLifecycleStatus.Matched
            or RemittanceLifecycleStatus.AvailableForPosting)
        {
            record.Outbox.Add(Entry(RemittanceMessageTypes.Matched, now));
        }
        else if (record.Status == RemittanceLifecycleStatus.Unmatched)
        {
            record.Outbox.Add(Entry(RemittanceMessageTypes.Unmatched, now));
        }
    }

    private static RemittanceOutboxEntry Entry(string type, DateTimeOffset now) =>
        new() { EventType = type, CreatedAtUtc = now };

    private async Task<bool> PublishPendingAsync(RemittanceReceipt record, CancellationToken ct)
    {
        EnsureOutbox(record, _timeProvider.GetUtcNow());
        if (_messageBus is null)
        {
            foreach (var entry in record.Outbox.Where(e => e.PublishedAtUtc is null))
            {
                entry.PublishedAtUtc = _timeProvider.GetUtcNow();
            }

            return true;
        }

        var message = new RemittanceReceivedMessage
        {
            RemittanceId = record.RemittanceId,
            Gateway = record.Gateway,
            TenantId = record.TenantId,
            Status = record.Status,
            ClaimCount = record.Claims.Count,
            MatchedClaimCount = record.Claims.Count(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched),
            PaymentAmount = record.PaymentAmount,
            CorrelationId = record.CorrelationId
        };

        var allPublished = true;
        foreach (var entry in record.Outbox.Where(e => e.PublishedAtUtc is null))
        {
            entry.AttemptCount++;
            try
            {
                await _messageBus.SendAsync(
                    RemittanceEventTopics.TopicName,
                    message,
                    new SendOptions(
                        MessageId: $"{record.Gateway}:{record.RemittanceId}:{entry.EventType}",
                        CorrelationId: record.CorrelationId,
                        Properties: new Dictionary<string, string>
                        {
                            [RemittanceEventTopics.MessageTypeProperty] = entry.EventType
                        }),
                    ct).ConfigureAwait(false);
                entry.PublishedAtUtc = _timeProvider.GetUtcNow();
                entry.LastError = null;
            }
            catch (Exception)
            {
                allPublished = false;
                entry.LastError = "publish-failed";
                record.LastErrorCategory = GatewayErrorCategory.ServiceUnavailable;
                record.LastError = "outbox-publish-failed";
                _logger.LogWarning(
                    "Failed to publish remittance event type={EventType} gateway={Gateway}",
                    Sanitize(entry.EventType), Sanitize(record.Gateway));
            }
        }

        return allPublished;
    }

    private void Log(RemittanceReceipt record, bool replay) =>
        _logger.LogInformation(
            "Remittance {Gateway} id={RemittanceId} tenant={TenantId} status={Status} " +
            "claims={ClaimCount} matched={Matched} replay={Replay} category={Category}",
            record.Gateway,
            Sanitize(record.RemittanceId),
            Sanitize(record.TenantId),
            record.Status,
            record.Claims.Count,
            record.Claims.Count(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched),
            replay,
            record.LastErrorCategory);

    private static void RecordMetric(RemittanceReceipt record, TimeSpan latency)
    {
        ChoMetrics.Remittances.Add(1,
            new KeyValuePair<string, object?>("cho.gateway", record.Gateway),
            new KeyValuePair<string, object?>("cho.status", record.Status.ToString()),
            new KeyValuePair<string, object?>("cho.error_category", record.LastErrorCategory.ToString()));
        ChoMetrics.RemittanceDuration.Record(
            latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.gateway", record.Gateway),
            new KeyValuePair<string, object?>("cho.status", record.Status.ToString()));
        ChoMetrics.RemittedClaims.Add(record.Claims.Count,
            new KeyValuePair<string, object?>("cho.gateway", record.Gateway),
            new KeyValuePair<string, object?>("cho.match",
                record.Claims.Count(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched) > 0
                    ? "matched"
                    : "unmatched"));
    }

    private static string? Sanitize(string? value) => ClaimAttachmentRules.SanitizeForLog(value);
}
