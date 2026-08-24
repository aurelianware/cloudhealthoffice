using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Applies a canonical 277CA to the durable transmission store. Tenant identity
/// always comes from the matched transmission. Duplicate deliveries are no-ops.
/// </summary>
public sealed class ClaimAcknowledgmentProcessor : IClaimAcknowledgmentProcessor
{
    private readonly IClaimAcknowledgmentStore _acknowledgments;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IMessageBus? _messageBus;
    private readonly ILogger<ClaimAcknowledgmentProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public ClaimAcknowledgmentProcessor(
        IClaimAcknowledgmentStore acknowledgments,
        IClaimTransmissionStore transmissions,
        ILogger<ClaimAcknowledgmentProcessor> logger,
        IMessageBus? messageBus = null,
        TimeProvider? timeProvider = null)
    {
        _acknowledgments = acknowledgments;
        _transmissions = transmissions;
        _messageBus = messageBus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ClaimAcknowledgmentProcessResult> ProcessAsync(
        GatewayClaimAcknowledgment acknowledgment,
        CancellationToken cancellationToken = default)
    {
        var started = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(acknowledgment.Gateway))
        {
            acknowledgment.Gateway = "unknown";
        }

        if (string.IsNullOrWhiteSpace(acknowledgment.AcknowledgmentId))
        {
            acknowledgment.AcknowledgmentId =
                acknowledgment.ExternalTransactionId ??
                acknowledgment.EventId ??
                Guid.NewGuid().ToString("N");
        }

        if (!string.IsNullOrWhiteSpace(acknowledgment.EventId))
        {
            var byEvent = await _acknowledgments
                .GetByEventIdAsync(acknowledgment.Gateway, acknowledgment.EventId, cancellationToken)
                .ConfigureAwait(false);
            if (byEvent is not null)
            {
                return await ReplayAsync(byEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        var existing = await _acknowledgments
            .GetByIdempotencyKeyAsync(
                acknowledgment.Gateway, acknowledgment.AcknowledgmentId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await ReplayAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        if (acknowledgment.Status == ClaimAcknowledgmentStatus.Malformed)
        {
            var malformed = ToRecord(acknowledgment, transmission: null);
            malformed.UnmatchedReason = "malformed";
            malformed.Status = ClaimAcknowledgmentStatus.Malformed;
            return await CommitNewAsync(malformed, transmission: null, started, cancellationToken)
                .ConfigureAwait(false);
        }

        var match = await ClaimAcknowledgmentMatcher
            .MatchAsync(acknowledgment, _transmissions, cancellationToken)
            .ConfigureAwait(false);

        if (match.Ambiguous || match.Transmission is null)
        {
            acknowledgment.Status = ClaimAcknowledgmentStatus.UnableToMatch;
            var unmatched = ToRecord(acknowledgment, transmission: null);
            unmatched.UnmatchedReason = match.Ambiguous ? "ambiguous-match" : match.Reason ?? "unmatched";
            unmatched.Status = ClaimAcknowledgmentStatus.UnableToMatch;
            unmatched.TenantId = string.Empty;
            return await CommitNewAsync(unmatched, transmission: null, started, cancellationToken)
                .ConfigureAwait(false);
        }

        var transmission = match.Transmission;
        var record = ToRecord(acknowledgment, transmission);
        record.TenantId = transmission.TenantId;
        record.ClaimId = transmission.ClaimId;
        record.ClaimType = transmission.ClaimType;
        record.TransmissionId = transmission.TransmissionId;
        return await CommitNewAsync(record, transmission, started, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClaimAcknowledgmentProcessResult> CommitNewAsync(
        ClaimAcknowledgmentRecord record,
        ClaimTransmissionRecord? transmission,
        DateTimeOffset started,
        CancellationToken ct)
    {
        var (created, stored) = await _acknowledgments.TryCreateAsync(record, ct).ConfigureAwait(false);
        if (!created)
        {
            return await ReplayAsync(stored, ct).ConfigureAwait(false);
        }

        if (transmission is not null)
        {
            await ApplyTransmissionAsync(stored, transmission, ct).ConfigureAwait(false);
        }

        var published = await PublishAsync(stored, transmission, ct).ConfigureAwait(false);
        stored.EventsPublished = published;
        await _acknowledgments.SaveAsync(stored, ct).ConfigureAwait(false);
        Log(stored, replay: false);
        RecordMetric(stored.Status, started);
        return ToResult(stored, replay: false, events: published, transmission?.Status);
    }

    private static ClaimAcknowledgmentRecord ToRecord(
        GatewayClaimAcknowledgment acknowledgment,
        ClaimTransmissionRecord? transmission) =>
        new()
        {
            AcknowledgmentId = acknowledgment.AcknowledgmentId,
            Gateway = acknowledgment.Gateway,
            EventId = acknowledgment.EventId,
            TransmissionId = transmission?.TransmissionId ?? acknowledgment.TransmissionId,
            TenantId = transmission?.TenantId ?? string.Empty,
            ClaimId = transmission?.ClaimId ?? acknowledgment.ClaimId,
            ClaimType = transmission?.ClaimType ?? acknowledgment.ClaimType,
            ReceivedAtUtc = acknowledgment.ReceivedAt == default
                ? DateTimeOffset.UtcNow
                : acknowledgment.ReceivedAt,
            Status = acknowledgment.Status,
            ExternalTransactionId = acknowledgment.ExternalTransactionId,
            OriginalSubmissionId = acknowledgment.OriginalSubmissionId,
            ClaimControlNumber = acknowledgment.ClaimControlNumber,
            PatientControlNumber = acknowledgment.PatientControlNumber ?? transmission?.PatientControlNumber,
            CorrelationId = acknowledgment.CorrelationId ?? transmission?.CorrelationId,
            RawSourceReference = acknowledgment.RawSourceReference,
            Errors = acknowledgment.Errors.ToList(),
            Warnings = acknowledgment.Warnings.ToList(),
            ServiceLineResults = acknowledgment.ServiceLineResults.ToList(),
            ClaimLevelResults = acknowledgment.ClaimLevelResults.ToList()
        };

    private static GatewayClaimTransmissionStatus? MapTransmissionStatus(
        ClaimAcknowledgmentStatus status,
        GatewayClaimTransmissionStatus current) =>
        status switch
        {
            ClaimAcknowledgmentStatus.Accepted or ClaimAcknowledgmentStatus.AcceptedWithWarnings
                => GatewayClaimTransmissionStatus.AcknowledgmentAccepted,
            ClaimAcknowledgmentStatus.Rejected
                => GatewayClaimTransmissionStatus.AcknowledgmentRejected,
            ClaimAcknowledgmentStatus.Partial
                => GatewayClaimTransmissionStatus.AcknowledgmentPartial,
            ClaimAcknowledgmentStatus.Malformed
                => current is GatewayClaimTransmissionStatus.AcknowledgmentAccepted
                    or GatewayClaimTransmissionStatus.AcknowledgmentRejected
                    or GatewayClaimTransmissionStatus.AcknowledgmentPartial
                    ? null
                    : GatewayClaimTransmissionStatus.AcknowledgmentFailed,
            _ => null
        };

    private async Task ApplyTransmissionAsync(
        ClaimAcknowledgmentRecord record,
        ClaimTransmissionRecord transmission,
        CancellationToken ct)
    {
        var submittedAt = transmission.SubmittedAtUtc;
        var nextStatus = MapTransmissionStatus(record.Status, transmission.Status);
        if (nextStatus is null)
        {
            return;
        }

        transmission.Status = nextStatus.Value;
        transmission.AcknowledgedAtUtc = record.ReceivedAtUtc;
        transmission.SubmittedAtUtc = submittedAt;
        await _transmissions.SaveAsync(transmission, ct).ConfigureAwait(false);
    }

    private async Task<ClaimAcknowledgmentProcessResult> ReplayAsync(
        ClaimAcknowledgmentRecord existing, CancellationToken ct)
    {
        ClaimTransmissionRecord? transmission = null;
        if (!string.IsNullOrWhiteSpace(existing.TransmissionId))
        {
            transmission = await _transmissions.GetByIdAsync(existing.TransmissionId, ct).ConfigureAwait(false);
            if (transmission is not null)
            {
                await ApplyTransmissionAsync(existing, transmission, ct).ConfigureAwait(false);
            }
        }

        var published = existing.EventsPublished;
        if (!published)
        {
            published = await PublishAsync(existing, transmission, ct).ConfigureAwait(false);
            if (published)
            {
                existing.EventsPublished = true;
                await _acknowledgments.SaveAsync(existing, ct).ConfigureAwait(false);
            }
        }

        Log(existing, replay: true);
        return ToResult(existing, replay: true, events: published, transmission?.Status);
    }

    private static ClaimAcknowledgmentProcessResult ToResult(
        ClaimAcknowledgmentRecord record,
        bool replay,
        bool events,
        GatewayClaimTransmissionStatus? transmissionStatus = null) =>
        new()
        {
            Replay = replay,
            Status = record.Status,
            AcknowledgmentId = record.AcknowledgmentId,
            TransmissionId = record.TransmissionId,
            TenantId = record.TenantId,
            TransmissionStatus = transmissionStatus,
            ErrorCategory = record.Status switch
            {
                ClaimAcknowledgmentStatus.UnableToMatch => GatewayErrorCategory.UnableToMatchTransmission,
                ClaimAcknowledgmentStatus.Malformed => GatewayErrorCategory.MalformedResponse,
                _ => GatewayErrorCategory.None
            },
            EventsPublished = events && !replay
        };

    private async Task<bool> PublishAsync(
        ClaimAcknowledgmentRecord record,
        ClaimTransmissionRecord? transmission,
        CancellationToken ct)
    {
        if (_messageBus is null || record.EventsPublished)
        {
            return record.EventsPublished;
        }

        var message = new ClaimAcknowledgmentReceivedMessage
        {
            AcknowledgmentId = record.AcknowledgmentId,
            Gateway = record.Gateway,
            TenantId = record.TenantId,
            TransmissionId = record.TransmissionId,
            ClaimId = record.ClaimId,
            Status = record.Status,
            TransmissionStatus = transmission?.Status,
            CorrelationId = record.CorrelationId
        };

        try
        {
            await Send(ClaimAcknowledgmentMessageTypes.Received, message, ct).ConfigureAwait(false);
            if (record.Status is ClaimAcknowledgmentStatus.Accepted
                or ClaimAcknowledgmentStatus.AcceptedWithWarnings)
            {
                await Send(ClaimAcknowledgmentMessageTypes.Accepted, message, ct).ConfigureAwait(false);
            }
            else if (record.Status == ClaimAcknowledgmentStatus.Rejected)
            {
                await Send(ClaimAcknowledgmentMessageTypes.Rejected, message, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish claim acknowledgment event ack={AcknowledgmentId} gateway={Gateway}",
                Sanitize(record.AcknowledgmentId), Sanitize(record.Gateway));
            return false;
        }
    }

    private Task Send(string type, ClaimAcknowledgmentReceivedMessage message, CancellationToken ct) =>
        _messageBus!.SendAsync(
            ClaimAcknowledgmentEventTopics.TopicName,
            message,
            new SendOptions(
                MessageId: $"{message.Gateway}:{message.AcknowledgmentId}:{type}",
                CorrelationId: message.CorrelationId,
                Properties: new Dictionary<string, string>
                {
                    [ClaimAcknowledgmentEventTopics.MessageTypeProperty] = type
                }),
            ct);

    private void Log(ClaimAcknowledgmentRecord record, bool replay) =>
        _logger.LogInformation(
            "Claim acknowledgment {Gateway} ack={AcknowledgmentId} transmission={TransmissionId} " +
            "tenant={TenantId} status={Status} replay={Replay}",
            Sanitize(record.Gateway),
            Sanitize(record.AcknowledgmentId),
            Sanitize(record.TransmissionId),
            Sanitize(record.TenantId),
            record.Status,
            replay);

    private static void RecordMetric(ClaimAcknowledgmentStatus status, DateTimeOffset started)
    {
        ChoMetrics.ClaimAcknowledgments.Add(1,
            new KeyValuePair<string, object?>("cho.status", status.ToString()));
        ChoMetrics.ClaimAcknowledgmentDuration.Record(
            Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds),
            new KeyValuePair<string, object?>("cho.status", status.ToString()));
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Strip control characters and anything that is not a safe identifier
        // token so log forging from inbound 277CA ids is not possible.
        var chars = value.Where(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '|').ToArray();
        return new string(chars);
    }
}
