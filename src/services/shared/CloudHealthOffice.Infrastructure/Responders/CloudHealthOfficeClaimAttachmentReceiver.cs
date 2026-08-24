using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Responders;

public sealed class CloudHealthOfficeClaimAttachmentReceiver : IClaimAttachmentReceiver
{
    public const string AdapterName = "canonical";

    private readonly IPayerEligibilityRouter _router;
    private readonly IPayerClaimDirectory _claims;
    private readonly IClaimAttachmentContentStore _content;
    private readonly IInboundClaimAttachmentReceiptStore _receipts;
    private readonly ClaimAttachmentOptions _attachmentOptions;
    private readonly ILogger<CloudHealthOfficeClaimAttachmentReceiver> _logger;
    private readonly TimeProvider _clock;
    private readonly IMessageBus? _bus;
    private readonly IInboundAttachmentScanner _scanner;

    public CloudHealthOfficeClaimAttachmentReceiver(
        IPayerEligibilityRouter router,
        IPayerClaimDirectory claims,
        IClaimAttachmentContentStore content,
        IInboundClaimAttachmentReceiptStore receipts,
        ILogger<CloudHealthOfficeClaimAttachmentReceiver> logger,
        IOptions<HealthcareTransactionOptions>? transactionOptions = null,
        TimeProvider? clock = null,
        IMessageBus? bus = null,
        IInboundAttachmentScanner? scanner = null)
    {
        _router = router;
        _claims = claims;
        _content = content;
        _receipts = receipts;
        _logger = logger;
        _attachmentOptions = transactionOptions?.Value.ClaimAttachments ?? new ClaimAttachmentOptions();
        _clock = clock ?? TimeProvider.System;
        _bus = bus;
        _scanner = scanner ?? NullInboundAttachmentScanner.Instance;
    }

    public async Task<GatewayResponse<InboundClaimAttachmentResult>> ReceiveAsync(
        InboundClaimAttachment attachment,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var started = _clock.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();
        attachment.ReceivedAt = attachment.ReceivedAt == default ? started : attachment.ReceivedAt;
        var adapter = string.IsNullOrWhiteSpace(attachment.AdapterName)
            ? AdapterName
            : attachment.AdapterName.Trim();

        var route = _router.ResolveIdentity(
            attachment.PayerId, attachment.TradingPartnerId, attachment.AuthenticatedEndpointId);
        if (!route.IsResolved)
        {
            var category = route.Status == EligibilityBusinessStatus.AmbiguousPayer
                ? GatewayErrorCategory.AmbiguousPayer
                : GatewayErrorCategory.InvalidPayer;
            return Finish(FailResult(null, category, route.Message ?? "Payer could not be resolved.", adapter),
                started, stopwatch, adapter);
        }

        var contentType = ClaimAttachmentRules.NormalizeContentType(attachment.ContentType);
        if (!ClaimAttachmentRules.IsSupportedContentType(contentType, _attachmentOptions))
        {
            return Finish(FailResult(route.TenantId, GatewayErrorCategory.UnsupportedContentType,
                "Attachment content type is not supported.", adapter, route.CanonicalPayerId),
                started, stopwatch, adapter);
        }

        await using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        if (copy.Length <= 0)
        {
            return Finish(FailResult(route.TenantId, GatewayErrorCategory.Validation,
                "Attachment content length must be greater than zero.", adapter, route.CanonicalPayerId),
                started, stopwatch, adapter);
        }

        if (copy.Length > _attachmentOptions.EffectiveMaxBytes())
        {
            return Finish(FailResult(route.TenantId, GatewayErrorCategory.AttachmentTooLarge,
                "Attachment exceeds the configured maximum size.", adapter, route.CanonicalPayerId),
                started, stopwatch, adapter);
        }

        copy.Position = 0;
        var checksum = await ClaimAttachmentRules.ComputeSha256HexAsync(copy, cancellationToken).ConfigureAwait(false);
        var key = IdempotencyKey(route.TenantId!, route.CanonicalPayerId, adapter, attachment, checksum);
        var existing = await _receipts.GetByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Finish(ToResult(existing, replay: true, claim: null), started, stopwatch, adapter);
        }

        ClaimAttachmentContentReference stored;
        try
        {
            copy.Position = 0;
            stored = await _content.StoreAsync(
                new ClaimAttachmentStoreRequest
                {
                    TenantId = route.TenantId!,
                    TransmissionId = "inbound",
                    AttachmentId = FirstNonBlank(
                        attachment.AttachmentControlNumber,
                        checksum[..Math.Min(12, checksum.Length)],
                        attachment.InboundAttachmentId ?? "att"),
                    ContentType = contentType,
                    DisplayName = attachment.FileName,
                    ScanStatus = attachment.Content?.ScanStatus ?? ClaimAttachmentScanStatus.Unknown
                },
                copy,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            var category = ex.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase)
                ? GatewayErrorCategory.AttachmentTooLarge
                : GatewayErrorCategory.Validation;
            return Finish(FailResult(route.TenantId, category, ex.Message, adapter, route.CanonicalPayerId),
                started, stopwatch, adapter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Inbound attachment storage failed adapter={Adapter}",
                ClaimAttachmentRules.SanitizeForLog(adapter));
            return Finish(FailResult(route.TenantId, GatewayErrorCategory.StorageUnavailable,
                "Secure attachment storage is unavailable.", adapter, route.CanonicalPayerId),
                started, stopwatch, adapter);
        }

        if (!string.IsNullOrWhiteSpace(attachment.SuppliedChecksumSha256) &&
            !string.Equals(attachment.SuppliedChecksumSha256, stored.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Finish(await PersistAsync(
                    attachment, route, stored, adapter, started,
                    InboundClaimAttachmentStatus.Quarantined,
                    GatewayErrorCategory.ChecksumMismatch,
                    "Supplied checksum does not match stored content.",
                    claim: null, line: null, matching: null, cancellationToken)
                .ConfigureAwait(false), started, stopwatch, adapter);
        }

        var scan = await _scanner.EvaluateAsync(stored, cancellationToken).ConfigureAwait(false);
        stored.ScanStatus = scan;
        if (scan is ClaimAttachmentScanStatus.Quarantined
            or ClaimAttachmentScanStatus.Unsafe
            or ClaimAttachmentScanStatus.ScanFailed)
        {
            return Finish(await PersistAsync(
                    attachment, route, stored, adapter, started,
                    InboundClaimAttachmentStatus.Quarantined,
                    GatewayErrorCategory.AttachmentUnsafe,
                    "Attachment content did not pass content-safety screening.",
                    claim: null, line: null, matching: null, cancellationToken)
                .ConfigureAwait(false), started, stopwatch, adapter);
        }

        var match = await _claims.FindAsync(
            new PayerClaimLookup
            {
                TenantId = route.TenantId!,
                CanonicalPayerId = route.CanonicalPayerId!,
                ClaimId = attachment.ClaimId,
                ClaimControlNumber = string.IsNullOrWhiteSpace(attachment.ClaimId) ? attachment.ClaimControlNumber : null,
                PatientControlNumber = string.IsNullOrWhiteSpace(attachment.ClaimId) &&
                                       string.IsNullOrWhiteSpace(attachment.ClaimControlNumber)
                    ? attachment.PatientControlNumber
                    : null,
                AttachmentControlNumber = string.IsNullOrWhiteSpace(attachment.ClaimId) &&
                                          string.IsNullOrWhiteSpace(attachment.ClaimControlNumber) &&
                                          string.IsNullOrWhiteSpace(attachment.PatientControlNumber)
                    ? attachment.AttachmentControlNumber
                    : null
            },
            cancellationToken).ConfigureAwait(false);

        if (match.None)
        {
            return Finish(await PersistAsync(
                    attachment, route, stored, adapter, started,
                    InboundClaimAttachmentStatus.Quarantined,
                    GatewayErrorCategory.UnableToMatch,
                    "No unique payer-side claim matched the supplied identifiers.",
                    claim: null, line: null, matching: null, cancellationToken)
                .ConfigureAwait(false), started, stopwatch, adapter);
        }

        if (match.Ambiguous)
        {
            return Finish(await PersistAsync(
                    attachment, route, stored, adapter, started,
                    InboundClaimAttachmentStatus.Quarantined,
                    GatewayErrorCategory.AmbiguousClaim,
                    "Multiple payer-side claims matched the supplied identifiers.",
                    claim: null, line: null, matching: null, cancellationToken)
                .ConfigureAwait(false), started, stopwatch, adapter);
        }

        var claim = match.Unique!;
        var matchingIdentifier = !string.IsNullOrWhiteSpace(attachment.ClaimId) ? "ClaimId"
            : !string.IsNullOrWhiteSpace(attachment.ClaimControlNumber) ? "ClaimControlNumber"
            : !string.IsNullOrWhiteSpace(attachment.PatientControlNumber) ? "PatientControlNumber"
            : "AttachmentControlNumber";

        int? lineNumber = null;
        if (attachment.ServiceLineNumber.HasValue || !string.IsNullOrWhiteSpace(attachment.ServiceLineControlNumber))
        {
            var lines = claim.ServiceLines.Where(l =>
                (!attachment.ServiceLineNumber.HasValue || l.LineNumber == attachment.ServiceLineNumber.Value) &&
                (string.IsNullOrWhiteSpace(attachment.ServiceLineControlNumber) ||
                 string.Equals(l.LineControlNumber, attachment.ServiceLineControlNumber, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (lines.Count == 0)
            {
                return Finish(await PersistAsync(
                        attachment, route, stored, adapter, started,
                        InboundClaimAttachmentStatus.Quarantined,
                        GatewayErrorCategory.ServiceLineNotFound,
                        "Service line was not present on the matched claim.",
                        claim, null, matchingIdentifier, cancellationToken)
                    .ConfigureAwait(false), started, stopwatch, adapter);
            }

            if (lines.Count > 1)
            {
                return Finish(await PersistAsync(
                        attachment, route, stored, adapter, started,
                        InboundClaimAttachmentStatus.Quarantined,
                        GatewayErrorCategory.AmbiguousServiceLine,
                        "Multiple service lines matched the supplied line identifier.",
                        claim, null, matchingIdentifier, cancellationToken)
                    .ConfigureAwait(false), started, stopwatch, adapter);
            }

            lineNumber = lines[0].LineNumber;
        }

        var result = await PersistAsync(
            attachment, route, stored, adapter, started,
            InboundClaimAttachmentStatus.AvailableToClaim,
            GatewayErrorCategory.None,
            null,
            claim, lineNumber, matchingIdentifier, cancellationToken).ConfigureAwait(false);

        if (!result.Replay && result.Status == InboundClaimAttachmentStatus.AvailableToClaim)
        {
            await _claims.MarkDocumentationReceivedAsync(claim.TenantId, claim.ClaimId, cancellationToken)
                .ConfigureAwait(false);
        }

        return Finish(result, started, stopwatch, adapter);
    }

    public async Task DispatchPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        var pending = await _receipts.ListPendingOutboxAsync(take, cancellationToken).ConfigureAwait(false);
        foreach (var record in pending)
        {
            await PublishOutboxAsync(record, replay: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InboundClaimAttachmentResult> PersistAsync(
        InboundClaimAttachment attachment,
        PayerEligibilityRouteResolution route,
        ClaimAttachmentContentReference stored,
        string adapter,
        DateTimeOffset started,
        InboundClaimAttachmentStatus status,
        GatewayErrorCategory error,
        string? errorMessage,
        PayerDirectoryClaim? claim,
        int? line,
        string? matching,
        CancellationToken ct)
    {
        var key = IdempotencyKey(route.TenantId ?? string.Empty, route.CanonicalPayerId, adapter, attachment, stored.ChecksumSha256);
        var existing = await _receipts.GetByIdempotencyKeyAsync(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return ToResult(existing, replay: true, claim);
        }

        var record = new InboundClaimAttachmentReceipt
        {
            IdempotencyKey = key,
            TenantId = route.TenantId ?? string.Empty,
            CanonicalPayerId = route.CanonicalPayerId,
            ClaimId = claim?.ClaimId,
            ServiceLineNumber = line,
            ExternalTransactionId = attachment.ExternalTransactionId,
            AttachmentControlNumber = attachment.AttachmentControlNumber,
            AttachmentType = attachment.AttachmentType,
            Mode = attachment.Mode,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            ChecksumSha256 = stored.ChecksumSha256,
            ContentContainer = stored.Container,
            ContentStorageKey = stored.StorageKey,
            SourceAdapter = adapter,
            Status = status,
            AssociationMethod = claim is null ? InboundClaimAssociationMethod.None : InboundClaimAssociationMethod.Deterministic,
            MatchingIdentifier = matching,
            ReceivedAtUtc = started,
            MatchedAtUtc = status is InboundClaimAttachmentStatus.AvailableToClaim or InboundClaimAttachmentStatus.Matched
                ? started
                : null,
            ErrorCategory = error,
            ErrorMessage = errorMessage
        };
        EnsureOutbox(record, started);

        var (created, storedRecord) = await _receipts.TryCreateAsync(record, ct).ConfigureAwait(false);
        if (!created)
        {
            return ToResult(storedRecord, replay: true, claim);
        }

        await PublishOutboxAsync(storedRecord, replay: false, ct).ConfigureAwait(false);
        return ToResult(storedRecord, replay: false, claim);
    }

    private static void EnsureOutbox(InboundClaimAttachmentReceipt record, DateTimeOffset now)
    {
        record.Outbox.Add(new InboundAttachmentOutboxEntry
        {
            EventType = InboundClaimAttachmentMessageTypes.Received,
            CreatedAtUtc = now
        });
        record.Outbox.Add(new InboundAttachmentOutboxEntry
        {
            EventType = record.Status == InboundClaimAttachmentStatus.Quarantined
                ? InboundClaimAttachmentMessageTypes.Quarantined
                : InboundClaimAttachmentMessageTypes.Matched,
            CreatedAtUtc = now
        });
    }

    private async Task PublishOutboxAsync(
        InboundClaimAttachmentReceipt record, bool replay, CancellationToken ct)
    {
        if (_bus is null)
        {
            foreach (var entry in record.Outbox.Where(e => e.PublishedAtUtc is null))
            {
                entry.PublishedAtUtc = _clock.GetUtcNow();
            }

            await _receipts.SaveAsync(record, ct).ConfigureAwait(false);
            return;
        }

        var now = _clock.GetUtcNow();
        foreach (var entry in record.Outbox.Where(e => e.PublishedAtUtc is null))
        {
            try
            {
                await _bus.SendAsync(
                    InboundClaimAttachmentEventTopics.TopicName,
                    new InboundClaimAttachmentAuditMessage
                    {
                        MessageType = entry.EventType,
                        ReceiptId = record.ReceiptId,
                        TenantId = record.TenantId,
                        ClaimId = record.ClaimId,
                        Status = record.Status,
                        Adapter = record.SourceAdapter,
                        ContentType = record.ContentType,
                        ContentLength = record.ContentLength,
                        ChecksumPrefix = ClaimAttachmentRules.ChecksumPrefix(record.ChecksumSha256),
                        ErrorCategory = record.ErrorCategory,
                        Replay = replay
                    },
                    new SendOptions(
                        MessageId: $"{record.ReceiptId}:{entry.EventType}",
                        Properties: new Dictionary<string, string>
                        {
                            [InboundClaimAttachmentEventTopics.MessageTypeProperty] = entry.EventType
                        }),
                    ct).ConfigureAwait(false);
                entry.PublishedAtUtc = now;
            }
            catch (Exception ex)
            {
                entry.AttemptCount++;
                entry.LastError = "outbox-publish-failed";
                _logger.LogWarning(ex, "Inbound attachment outbox publish failed receipt={ReceiptId}",
                    ClaimAttachmentRules.SanitizeForLog(record.ReceiptId));
            }
        }

        await _receipts.SaveAsync(record, ct).ConfigureAwait(false);
    }

    private static string IdempotencyKey(
        string tenantId,
        string? canonicalPayerId,
        string adapter,
        InboundClaimAttachment attachment,
        string checksum)
    {
        var ext = attachment.ExternalTransactionId?.Trim() ?? string.Empty;
        var acn = attachment.AttachmentControlNumber?.Trim() ?? string.Empty;
        return $"{tenantId}|{canonicalPayerId}|{adapter}|{ext}|{acn}|{checksum}";
    }

    private static InboundClaimAttachmentResult ToResult(
        InboundClaimAttachmentReceipt record, bool replay, PayerDirectoryClaim? claim) =>
        new()
        {
            ReceiptId = record.ReceiptId,
            Status = record.Status,
            TenantId = record.TenantId,
            CanonicalPayerId = record.CanonicalPayerId,
            ClaimId = record.ClaimId,
            ServiceLineNumber = record.ServiceLineNumber,
            AttachmentControlNumber = record.AttachmentControlNumber,
            AttachmentType = record.AttachmentType,
            AssociationLevel = record.ServiceLineNumber.HasValue
                ? ClaimAttachmentAssociationLevel.ServiceLine
                : ClaimAttachmentAssociationLevel.Claim,
            AssociationMethod = record.AssociationMethod,
            MatchingIdentifier = record.MatchingIdentifier,
            ContentType = record.ContentType,
            ContentLength = record.ContentLength,
            ChecksumSha256 = record.ChecksumSha256,
            ContentStorageKey = record.ContentStorageKey,
            IdempotencyKey = record.IdempotencyKey,
            Replay = replay,
            AvailableToExaminer = record.Status == InboundClaimAttachmentStatus.AvailableToClaim,
            ClaimAdjudicated = claim?.IsAdjudicated == true,
            ClaimPaid = claim?.IsPaid == true,
            ErrorCategory = record.ErrorCategory,
            ErrorMessage = record.ErrorMessage
        };

    private static InboundClaimAttachmentResult FailResult(
        string? tenantId,
        GatewayErrorCategory category,
        string message,
        string adapter,
        string? canonicalPayerId = null) =>
        new()
        {
            Status = InboundClaimAttachmentStatus.Rejected,
            TenantId = tenantId,
            CanonicalPayerId = canonicalPayerId,
            AssociationLevel = ClaimAttachmentAssociationLevel.None,
            ErrorCategory = category,
            ErrorMessage = message
        };

    private GatewayResponse<InboundClaimAttachmentResult> Finish(
        InboundClaimAttachmentResult result,
        DateTimeOffset started,
        long stopwatch,
        string adapter)
    {
        var latency = Stopwatch.GetElapsedTime(stopwatch);
        var transportFailed = result.ErrorCategory is GatewayErrorCategory.StorageUnavailable
            or GatewayErrorCategory.Configuration;
        var metadata = new GatewayTransactionMetadata
        {
            GatewayName = adapter,
            TransactionType = HealthcareTransactionType.ClaimAttachment275,
            SubmittedAtUtc = started,
            CompletedAtUtc = started + latency,
            Status = transportFailed ? GatewayTransactionStatus.Failed : GatewayTransactionStatus.Completed,
            TenantId = result.TenantId ?? string.Empty,
            Latency = latency,
            ErrorCategory = result.ErrorCategory
        };
        Log(result, metadata);
        ChoMetrics.InboundClaimAttachments.Add(1,
            new KeyValuePair<string, object?>("cho.adapter", adapter),
            new KeyValuePair<string, object?>("cho.status", result.Status.ToString()),
            new KeyValuePair<string, object?>("cho.error_category", result.ErrorCategory.ToString()),
            new KeyValuePair<string, object?>("cho.association", result.AssociationLevel.ToString()));
        ChoMetrics.InboundClaimAttachmentDuration.Record(latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.adapter", adapter));

        if (transportFailed)
        {
            return GatewayResponse<InboundClaimAttachmentResult>.Failure(
                result.ErrorMessage ?? "Inbound attachment failed.", metadata);
        }

        return GatewayResponse<InboundClaimAttachmentResult>.Success(result, metadata);
    }

    private void Log(InboundClaimAttachmentResult result, GatewayTransactionMetadata metadata) =>
        _logger.LogInformation(
            "Inbound claim attachment adapter={Adapter} receipt={ReceiptId} status={Status} " +
            "category={ErrorCategory} contentType={ContentType} contentLength={ContentLength} " +
            "checksumPrefix={ChecksumPrefix} replay={Replay} matched={Matched}",
            ClaimAttachmentRules.SanitizeForLog(metadata.GatewayName),
            ClaimAttachmentRules.SanitizeForLog(result.ReceiptId),
            result.Status,
            result.ErrorCategory,
            ClaimAttachmentRules.SanitizeForLog(result.ContentType),
            result.ContentLength,
            ClaimAttachmentRules.ChecksumPrefix(result.ChecksumSha256),
            result.Replay,
            result.AvailableToExaminer);

    private static string FirstNonBlank(string? a, string? b, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(a))
        {
            return a.Trim();
        }

        if (!string.IsNullOrWhiteSpace(b))
        {
            return b.Trim();
        }

        return fallback;
    }
}

public interface IInboundAttachmentScanner
{
    Task<ClaimAttachmentScanStatus> EvaluateAsync(
        ClaimAttachmentContentReference content, CancellationToken ct = default);
}

public sealed class NullInboundAttachmentScanner : IInboundAttachmentScanner
{
    public static readonly NullInboundAttachmentScanner Instance = new();

    public Task<ClaimAttachmentScanStatus> EvaluateAsync(
        ClaimAttachmentContentReference content, CancellationToken ct = default) =>
        Task.FromResult(content.ScanStatus);
}
