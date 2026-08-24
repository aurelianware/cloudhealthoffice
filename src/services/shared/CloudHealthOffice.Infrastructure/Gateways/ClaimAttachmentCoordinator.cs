using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

internal sealed record ClaimAttachmentTransportResult(
    bool Accepted,
    string? ExternalTransactionId,
    int RetryCount,
    GatewayErrorCategory Category,
    string? ErrorMessage);

internal delegate Task<ClaimAttachmentTransportResult> ClaimAttachmentTransport(
    ClaimAttachmentSubmissionRequest request,
    ClaimAttachmentContentReference content,
    CancellationToken ct);

/// <summary>
/// Shared 275 submission pipeline: association, content integrity, idempotency,
/// lifecycle persistence, and PHI-safe logging. Vendor transport is injected.
/// </summary>
internal sealed class ClaimAttachmentCoordinator
{
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAttachmentTransmissionStore _attachments;
    private readonly IClaimAttachmentContentStore _content;
    private readonly ClaimAttachmentOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IMessageBus? _bus;

    public ClaimAttachmentCoordinator(
        IClaimTransmissionStore transmissions,
        IClaimAttachmentTransmissionStore attachments,
        IClaimAttachmentContentStore content,
        ClaimAttachmentOptions options,
        ILogger logger,
        TimeProvider? timeProvider = null,
        IMessageBus? bus = null)
    {
        _transmissions = transmissions;
        _attachments = attachments;
        _content = content;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bus = bus;
    }

    public async Task<GatewayResponse<ClaimAttachmentSubmissionResult>> SubmitAsync(
        string gatewayName,
        ClaimAttachmentSubmissionRequest request,
        ClaimAttachmentTransport transport,
        CancellationToken ct)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();

        var validation = ClaimAttachmentRules.ValidateRequest(request, _options);
        if (validation is not null)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0, validation.Value.Category, validation.Value.Message);
        }

        var transmission = await _transmissions.GetByIdAsync(request.TransmissionId, ct).ConfigureAwait(false);
        if (transmission is null)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0,
                GatewayErrorCategory.TransmissionNotFound, "Claim transmission was not found.");
        }

        var association = ClaimAttachmentRules.ValidateAssociation(request, transmission);
        if (association is not null)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0, association.Value.Category, association.Value.Message);
        }

        request.TenantId = transmission.TenantId;
        request.PayerId ??= transmission.PayerId;
        request.ContentType = ClaimAttachmentRules.NormalizeContentType(request.ContentType);
        if (string.IsNullOrEmpty(request.ContentType))
        {
            request.ContentType = ClaimAttachmentRules.NormalizeContentType(request.Content!.ContentType);
        }

        var content = request.Content!;
        if (!await _content.ExistsAsync(content, ct).ConfigureAwait(false))
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0,
                GatewayErrorCategory.AttachmentNotFound, "Attachment content was not found in secure storage.");
        }

        await using (var stream = await _content.OpenReadAsync(content, ct).ConfigureAwait(false))
        {
            var checksum = await ClaimAttachmentRules.ComputeSha256HexAsync(stream, ct).ConfigureAwait(false);
            if (!string.Equals(checksum, content.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(gatewayName, request, startedAt, stopwatch, 0,
                    GatewayErrorCategory.Validation, "Attachment checksum does not match stored content.");
            }
        }

        await PublishAsync(ClaimAttachmentMessageTypes.ReadForSubmission, gatewayName, request, null, ct)
            .ConfigureAwait(false);

        var key = request.ResolveIdempotencyKey();
        var existing = await _attachments.GetByIdempotencyKeyAsync(request.TenantId, key, ct).ConfigureAwait(false);
        if (existing is not null &&
            ClaimAttachmentTransmissionStatuses.PreventsDuplicateSubmit(existing.Status))
        {
            return Success(gatewayName, request, existing, startedAt, stopwatch, replay: true);
        }

        var sameId = (await _attachments.ListByClaimTransmissionIdAsync(transmission.TransmissionId, ct)
            .ConfigureAwait(false))
            .Where(r => string.Equals(r.AttachmentId, request.AttachmentId, StringComparison.Ordinal))
            .OrderByDescending(r => r.AttachmentVersion)
            .FirstOrDefault();
        if (sameId is not null &&
            ClaimAttachmentTransmissionStatuses.PreventsDuplicateSubmit(sameId.Status) &&
            !string.Equals(sameId.ChecksumSha256, content.ChecksumSha256, StringComparison.OrdinalIgnoreCase) &&
            request.AttachmentVersion <= sameId.AttachmentVersion)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Validation,
                "Submitted attachment content is immutable. Create a new attachment version to send changed content.");
        }

        var record = existing ?? new ClaimAttachmentTransmissionRecord
        {
            TenantId = transmission.TenantId,
            ClaimId = transmission.ClaimId,
            ClaimTransmissionId = transmission.TransmissionId,
            AttachmentId = request.AttachmentId,
            AttachmentVersion = request.AttachmentVersion,
            GatewayName = gatewayName,
            PayerId = transmission.PayerId,
            ClaimType = transmission.ClaimType,
            AttachmentType = request.AttachmentType,
            Mode = request.Mode,
            AssociationLevel = request.AssociationLevel,
            ServiceLineNumber = request.ServiceLineNumber,
            AttachmentControlNumber = request.AttachmentControlNumber,
            ContentType = request.ContentType,
            ContentLength = content.ContentLength,
            ChecksumSha256 = content.ChecksumSha256,
            ContentContainer = content.Container,
            ContentStorageKey = content.StorageKey,
            IdempotencyKey = key,
            CorrelationId = request.CorrelationId,
            SubmittedAtUtc = startedAt,
            Status = ClaimAttachmentTransmissionStatus.ReadyForSubmission
        };

        record.Status = ClaimAttachmentTransmissionStatus.Transmitting;
        if (existing is null)
        {
            var (created, stored) = await _attachments.TryCreateAsync(record, ct).ConfigureAwait(false);
            if (!created)
            {
                if (ClaimAttachmentTransmissionStatuses.PreventsDuplicateSubmit(stored.Status))
                {
                    return Success(gatewayName, request, stored, startedAt, stopwatch, replay: true);
                }

                record = stored;
                record.Status = ClaimAttachmentTransmissionStatus.Transmitting;
            }
        }
        else
        {
            await _attachments.SaveAsync(record, ct).ConfigureAwait(false);
        }

        ClaimAttachmentTransportResult sent;
        try
        {
            sent = await transport(request, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Status = ClaimAttachmentTransmissionStatus.Failed;
            record.ErrorCategory = GatewayErrorCategory.Internal;
            record.ErrorMessage = "Unexpected error submitting the claim attachment.";
            record.CompletedAtUtc = _timeProvider.GetUtcNow();
            await _attachments.SaveAsync(record, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Unexpected error submitting claim attachment {AttachmentId} transmission={TransmissionId}",
                Sanitize(request.AttachmentId), Sanitize(request.TransmissionId));
            return Fail(gatewayName, request, startedAt, stopwatch, record.RetryCount,
                GatewayErrorCategory.Internal, record.ErrorMessage);
        }

        record.RetryCount = sent.RetryCount;
        record.ExternalTransactionId = sent.ExternalTransactionId ?? record.ExternalTransactionId;
        record.CompletedAtUtc = _timeProvider.GetUtcNow();
        if (sent.Accepted)
        {
            record.Status = ClaimAttachmentTransmissionStatus.GatewayAccepted;
            record.ErrorCategory = GatewayErrorCategory.None;
            record.ErrorMessage = null;
        }
        else
        {
            record.Status = sent.Category is GatewayErrorCategory.Validation
                or GatewayErrorCategory.UnsupportedContentType
                or GatewayErrorCategory.AttachmentTooLarge
                or GatewayErrorCategory.PayerRejected
                or GatewayErrorCategory.GatewayRejected
                ? ClaimAttachmentTransmissionStatus.GatewayRejected
                : ClaimAttachmentTransmissionStatus.Failed;
            record.ErrorCategory = sent.Category;
            record.ErrorMessage = sent.ErrorMessage;
        }

        await _attachments.SaveAsync(record, ct).ConfigureAwait(false);
        await PublishAsync(ClaimAttachmentMessageTypes.Submitted, gatewayName, request, record, ct)
            .ConfigureAwait(false);
        await PublishAsync(ClaimAttachmentMessageTypes.TransmissionResult, gatewayName, request, record, ct)
            .ConfigureAwait(false);

        if (!sent.Accepted)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, sent.RetryCount, sent.Category,
                sent.ErrorMessage ?? "Attachment submission failed.");
        }

        return Success(gatewayName, request, record, startedAt, stopwatch, replay: false);
    }

    private GatewayResponse<ClaimAttachmentSubmissionResult> Success(
        string gatewayName,
        ClaimAttachmentSubmissionRequest request,
        ClaimAttachmentTransmissionRecord record,
        DateTimeOffset startedAt,
        long stopwatch,
        bool replay)
    {
        var result = new ClaimAttachmentSubmissionResult
        {
            AttachmentId = record.AttachmentId,
            AttachmentTransmissionId = record.AttachmentTransmissionId,
            TransmissionId = record.ClaimTransmissionId,
            ClaimId = record.ClaimId,
            Status = record.Status,
            AttachmentType = record.AttachmentType,
            Mode = record.Mode,
            AssociationLevel = record.AssociationLevel,
            ServiceLineNumber = record.ServiceLineNumber,
            ContentType = record.ContentType,
            ContentLength = record.ContentLength,
            ChecksumSha256 = record.ChecksumSha256,
            ExternalTransactionId = record.ExternalTransactionId,
            AttachmentControlNumber = record.AttachmentControlNumber,
            IdempotencyKey = record.IdempotencyKey,
            AcceptedForProcessing = ClaimAttachmentTransmissionStatuses.PreventsDuplicateSubmit(record.Status),
            ReplayOfExistingTransmission = replay
        };
        var metadata = Metadata(
            gatewayName, request, startedAt, Stopwatch.GetElapsedTime(stopwatch),
            GatewayTransactionStatus.Completed, GatewayErrorCategory.None, record.RetryCount,
            record.ExternalTransactionId);
        Log(metadata, request, record.ChecksumSha256, record.ContentLength);
        RecordMetric(gatewayName, record.Status, GatewayErrorCategory.None, metadata.Latency);
        return GatewayResponse<ClaimAttachmentSubmissionResult>.Success(result, metadata);
    }

    private GatewayResponse<ClaimAttachmentSubmissionResult> Fail(
        string gatewayName,
        ClaimAttachmentSubmissionRequest request,
        DateTimeOffset startedAt,
        long stopwatch,
        int retryCount,
        GatewayErrorCategory category,
        string message)
    {
        var status = category switch
        {
            GatewayErrorCategory.Timeout => GatewayTransactionStatus.TimedOut,
            GatewayErrorCategory.GatewayRejected or GatewayErrorCategory.PayerRejected =>
                GatewayTransactionStatus.Rejected,
            _ => GatewayTransactionStatus.Failed
        };
        var metadata = Metadata(
            gatewayName, request, startedAt, Stopwatch.GetElapsedTime(stopwatch),
            status, category, retryCount, null);
        Log(metadata, request, request.Content?.ChecksumSha256, request.ContentLength);
        RecordMetric(gatewayName, ClaimAttachmentTransmissionStatus.Failed, category, metadata.Latency);
        return GatewayResponse<ClaimAttachmentSubmissionResult>.Failure(message, metadata);
    }

    private static GatewayTransactionMetadata Metadata(
        string gatewayName,
        ClaimAttachmentSubmissionRequest request,
        DateTimeOffset startedAt,
        TimeSpan latency,
        GatewayTransactionStatus status,
        GatewayErrorCategory category,
        int retryCount,
        string? externalTransactionId) =>
        new()
        {
            GatewayName = gatewayName,
            TransactionType = HealthcareTransactionType.ClaimAttachment275,
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + latency,
            Status = status,
            ExternalTransactionId = externalTransactionId,
            CorrelationId = request.CorrelationId,
            TenantId = request.TenantId,
            Latency = latency,
            RetryCount = retryCount,
            ErrorCategory = category
        };

    private void Log(
        GatewayTransactionMetadata metadata,
        ClaimAttachmentSubmissionRequest request,
        string? checksum,
        long contentLength) =>
        _logger.LogInformation(
            "Gateway transaction {Gateway} {TransactionType} attachment={AttachmentId} transmission={TransmissionId} " +
            "contentType={ContentType} contentLength={ContentLength} checksumPrefix={ChecksumPrefix} " +
            "status={Status} category={ErrorCategory} latencyMs={LatencyMs} retries={RetryCount} extId={ExternalTransactionId}",
            metadata.GatewayName,
            metadata.TransactionType,
            Sanitize(request.AttachmentId),
            Sanitize(request.TransmissionId),
            Sanitize(ClaimAttachmentRules.NormalizeContentType(request.ContentType)),
            contentLength,
            ClaimAttachmentRules.ChecksumPrefix(checksum),
            metadata.Status,
            metadata.ErrorCategory,
            metadata.Latency.TotalMilliseconds,
            metadata.RetryCount,
            Sanitize(metadata.ExternalTransactionId));

    private static void RecordMetric(
        string gatewayName,
        ClaimAttachmentTransmissionStatus status,
        GatewayErrorCategory category,
        TimeSpan latency)
    {
        ChoMetrics.ClaimAttachments.Add(1,
            new KeyValuePair<string, object?>("cho.gateway", gatewayName),
            new KeyValuePair<string, object?>("cho.status", status.ToString()),
            new KeyValuePair<string, object?>("cho.error_category", category.ToString()));
        ChoMetrics.ClaimAttachmentDuration.Record(latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.gateway", gatewayName));
    }

    private async Task PublishAsync(
        string messageType,
        string gatewayName,
        ClaimAttachmentSubmissionRequest request,
        ClaimAttachmentTransmissionRecord? record,
        CancellationToken ct)
    {
        if (_bus is null)
        {
            return;
        }

        try
        {
            await _bus.SendAsync(
                ClaimAttachmentEventTopics.TopicName,
                new ClaimAttachmentAuditMessage
                {
                    MessageType = messageType,
                    AttachmentId = request.AttachmentId,
                    AttachmentTransmissionId = record?.AttachmentTransmissionId ?? string.Empty,
                    Gateway = gatewayName,
                    TenantId = request.TenantId,
                    ClaimId = request.ClaimId,
                    ClaimTransmissionId = request.TransmissionId,
                    ContentType = request.ContentType,
                    ContentLength = record?.ContentLength ?? request.ContentLength,
                    ChecksumPrefix = ClaimAttachmentRules.ChecksumPrefix(
                        record?.ChecksumSha256 ?? request.Content?.ChecksumSha256),
                    Status = record?.Status ?? ClaimAttachmentTransmissionStatus.Stored,
                    ErrorCategory = record?.ErrorCategory ?? GatewayErrorCategory.None,
                    CorrelationId = request.CorrelationId
                },
                new SendOptions(
                    Properties: new Dictionary<string, string>
                    {
                        [ClaimAttachmentEventTopics.MessageTypeProperty] = messageType
                    }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claim attachment audit publish failed for attachment={AttachmentId}",
                Sanitize(request.AttachmentId));
        }
    }

    private static string? Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
