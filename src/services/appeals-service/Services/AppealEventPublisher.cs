using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppealsService.Models;
using Confluent.Kafka;

namespace AppealsService.Services;

/// <summary>
/// Kafka transport for <c>appeal.status-changed.v1</c>. Mirrors the shape
/// of <c>ConsentService.Services.ConsentEventPublisher</c> and
/// <c>PersonalRepresentativeService.Services.PersonalRepEventPublisher</c>:
/// - <see cref="IHostedService"/> + <see cref="IAsyncDisposable"/>.
/// - Disabled when <c>Kafka:BootstrapServers</c> is unset — the service
///   still boots; events stay in the appeal outbox.
/// - Single topic, ten event types distinguished by the <c>event-type</c>
///   header.
/// - Partition key = <c>appealId</c> (per-appeal ordering preserved).
/// - Headers: <c>tenant-id</c>, <c>event-type</c>, <c>event-version</c>,
///   <c>event-id</c> (de-duplication key) and <c>event-sequence</c>
///   (per-appeal order; also in the payload as <c>sequence</c>).
/// - A fatal producer error (<c>Error.IsFatal</c>) rebuilds the producer.
///
/// Only <c>AppealOutboxDispatcher</c> calls <see cref="ProduceAsync"/>, and
/// a failure is thrown, never swallowed: the dispatcher keeps the event in
/// the outbox and retries. The payload builders below are the field
/// whitelist for every event (see the wire-shape records).
/// </summary>
public sealed class AppealEventPublisher : IAppealEventTransport, IHostedService, IAsyncDisposable
{
    public const string StatusChangedTopic = "appeal.status-changed.v1";
    public const string EventVersion = "1.0";

    public const string AppealCreatedType = "AppealCreated";
    public const string AppealStatusChangedType = "AppealStatusChanged";
    public const string AppealClosedType = "AppealClosed";
    public const string AppealNoteAddedType = "AppealNoteAdded";
    public const string AppealAttachmentAddedType = "AppealAttachmentAdded";
    public const string AppealAttachmentAcknowledgedType = "AppealAttachmentAcknowledged";
    public const string AppealOverdueObservedType = "AppealOverdueObserved";
    public const string AppealAssignedType = "AppealAssigned";
    public const string AppealStatusMigratedType = "AppealStatusMigrated";
    public const string AppealDeadlineExtendedType = "AppealDeadlineExtended";

    private readonly ILogger<AppealEventPublisher> _logger;
    private readonly IConfiguration _configuration;
    private readonly object _producerSync = new();
    private ProducerConfig? _producerConfig;
    private volatile IProducer<string, string>? _producer;
    private volatile bool _available;
    private readonly TaskCompletionSource<AppealEventPublisherState> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public Task<AppealEventPublisherState> Started => _started.Task;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public AppealEventPublisher(ILogger<AppealEventPublisher> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning(
                "Kafka:BootstrapServers not configured — appeal event publishing disabled; events stay in the appeal outbox");
            _started.TrySetResult(AppealEventPublisherState.Disabled);
            return Task.CompletedTask;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = _configuration["Kafka:ClientId"] ?? "appeals-service",
            MessageTimeoutMs = 10_000,
            RequestTimeoutMs = 10_000,
            SocketTimeoutMs = 10_000,
            EnableIdempotence = true
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            producerConfig.SaslUsername = saslUsername;
            producerConfig.SaslPassword = _configuration["Kafka:SaslPassword"];
            producerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        _producerConfig = producerConfig;
        try
        {
            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            _available = true;
            _logger.LogInformation("Appeal event publisher connected to Kafka at {Servers}",
                LogSanitizer.SafeForLog(bootstrapServers));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — appeal events stay in the outbox until a restart succeeds");
            _producer?.Dispose();
            _producer = null;
            _available = false;
        }

        _started.TrySetResult(_available ? AppealEventPublisherState.Available : AppealEventPublisherState.Unavailable);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _available = false;
        try
        {
            _producer?.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing Kafka producer on shutdown");
        }
        _producer?.Dispose();
        _producer = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ProduceAsync(AppealOutboxMessage outbox, CancellationToken ct)
    {
        var producer = _producer;
        if (!_available || producer == null)
            throw new AppealEventTransportUnavailableException("Kafka producer is not available.");

        var headers = new Headers
        {
            { "tenant-id", Encoding.UTF8.GetBytes(outbox.TenantId) },
            { "event-type", Encoding.UTF8.GetBytes(outbox.EventType) },
            { "event-version", Encoding.UTF8.GetBytes(EventVersion) },
            { "event-id", Encoding.UTF8.GetBytes(outbox.EventId) }
        };
        if (outbox.Sequence is { } sequence)
            headers.Add("event-sequence", Encoding.UTF8.GetBytes(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var message = new Message<string, string>
        {
            Key = outbox.AppealId,
            Value = AppealOutbox.WirePayload(outbox),
            Headers = headers
        };

        try
        {
            // Throws ProduceException on a broker NACK or timeout; the
            // dispatcher records the failure and retries.
            await producer.ProduceAsync(StatusChangedTopic, message, ct);
        }
        catch (KafkaException ex) when (ex.Error.IsFatal)
        {
            // A fatal error leaves the idempotent producer unusable until it
            // is recreated; rebuild so the next attempt can succeed.
            RebuildProducer(producer, ex.Error);
            throw;
        }
    }

    private void RebuildProducer(IProducer<string, string> failed, Error error)
    {
        lock (_producerSync)
        {
            if (!ReferenceEquals(_producer, failed) || _producerConfig is null) return;
            _logger.LogError("Kafka producer hit a fatal error ({Code}: {Reason}); rebuilding it.",
                error.Code, LogSanitizer.SafeForLog(error.Reason));
            try
            {
                _producer = new ProducerBuilder<string, string>(_producerConfig).Build();
                _available = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rebuilding the Kafka producer failed; events stay pending until it succeeds.");
                _producer = null;
                _available = false;
            }
            try { failed.Dispose(); } catch (Exception) { /* already broken */ }
        }
    }

    /// <inheritdoc />
    public bool IsTransient(Exception error) => IsTransientError(error);

    /// <summary>
    /// Transient = the whole broker / cluster / credentials, which affects
    /// every event alike: an outage, a missing topic, a revoked ACL, failed
    /// SASL authentication, or a fatal producer error (the producer is
    /// rebuilt). These pause the relay and never dead-letter. Only errors
    /// about one message (too large, invalid record) are not transient.
    /// </summary>
    internal static bool IsTransientError(Exception error) => error switch
    {
        AppealEventTransportUnavailableException => true,
        OperationCanceledException => true,
        KafkaException k when k.Error.IsFatal => true,
        KafkaException k => k.Error.Code is ErrorCode.Local_MsgTimedOut
            or ErrorCode.UnknownTopicOrPart
            or ErrorCode.Local_UnknownTopic
            or ErrorCode.Local_UnknownPartition
            or ErrorCode.TopicAuthorizationFailed
            or ErrorCode.ClusterAuthorizationFailed
            or ErrorCode.SaslAuthenticationFailed
            or ErrorCode.Local_Authentication
            or ErrorCode.Local_Fatal
            or ErrorCode.Local_Transport
            or ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_TimedOut
            or ErrorCode.Local_QueueFull
            or ErrorCode.Local_Resolve
            or ErrorCode.RequestTimedOut
            or ErrorCode.NetworkException
            or ErrorCode.BrokerNotAvailable
            or ErrorCode.LeaderNotAvailable
            or ErrorCode.NotLeaderForPartition
            or ErrorCode.NotEnoughReplicas
            or ErrorCode.NotEnoughReplicasAfterAppend
            or ErrorCode.NotCoordinatorForGroup,
        _ => false
    };

    // ── Payload builders (internal for field-whitelist tests) ───────────

    internal static AppealCreatedEventPayload BuildCreatedPayload(Appeal a, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealCreatedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        AppealNumber = a.AppealNumber,
        ClaimId = a.ClaimId,
        ClaimNumber = a.ClaimNumber,
        MemberId = a.MemberId,
        ProviderNPI = a.ProviderNPI,
        AppealType = a.AppealType.ToString(),
        AppealLevel = a.AppealLevel.ToString(),
        LineOfBusiness = a.LineOfBusiness.ToString(),
        Source = a.Source.ToString(),
        TargetResponseDate = a.TargetResponseDate,
        IsUrgent = a.IsUrgent,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealStatusChangedEventPayload BuildStatusChangedPayload(
        Appeal a, AppealStatus from, AppealStatus to, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealStatusChangedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        FromStatus = from.ToString(),
        ToStatus = to.ToString(),
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealClosedEventPayload BuildClosedPayload(
        Appeal a, AppealStatus from, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealClosedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        FromStatus = from.ToString(),
        ClosureReasonCode = a.ClosureReasonCode?.ToString(),
        DecisionType = a.Decision?.DecisionType.ToString(),
        ApprovedAmount = a.Decision?.ApprovedAmount,
        DecisionDate = a.DecisionDate,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealNoteAddedEventPayload BuildNoteAddedPayload(
        Appeal a, AppealNote n, string actor, string? correlationId,
        string? eventId = null, DateTime? occurredAt = null) => new()
    {
        EventId = eventId ?? Guid.NewGuid().ToString(),
        EventType = AppealNoteAddedType,
        EventVersion = EventVersion,
        OccurredAt = occurredAt ?? DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        NoteId = n.NoteId,
        Author = n.CreatedBy,
        CreatedAt = n.CreatedAt,
        IsInternal = n.IsInternal,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealAttachmentAddedEventPayload BuildAttachmentAddedPayload(
        Appeal a, AppealAttachment att, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealAttachmentAddedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        AttachmentId = att.AttachmentId,
        AttachmentTypeCode = att.AttachmentTypeCode,
        TransmissionCode = att.TransmissionCode,
        ControlNumber = att.ControlNumber,
        UploadedAt = att.UploadedAt,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealAttachmentAcknowledgedEventPayload BuildAttachmentAcknowledgedPayload(
        Appeal a, AppealAttachment att, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealAttachmentAcknowledgedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        AttachmentId = att.AttachmentId,
        AcknowledgmentReceived = att.AcknowledgmentReceived,
        SentDate = att.SentDate,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealOverdueObservedEventPayload BuildOverdueObservedPayload(
        Appeal a, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealOverdueObservedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        CurrentStatus = a.Status.ToString(),
        TargetResponseDate = a.TargetResponseDate,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealAssignedEventPayload BuildAssignedPayload(
        Appeal a, string? previousReviewerId, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealAssignedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        AssignedReviewerId = a.AssignedReviewerId,
        PreviousReviewerId = previousReviewerId,
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealStatusMigratedEventPayload BuildStatusMigratedPayload(
        Appeal a, string legacyStatus, AppealClosureReasonCode mappedReasonCode,
        string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealStatusMigratedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        LegacyStatus = legacyStatus,
        MappedReasonCode = mappedReasonCode.ToString(),
        Actor = actor,
        CorrelationId = correlationId
    };

    internal static AppealDeadlineExtendedEventPayload BuildDeadlineExtendedPayload(
        Appeal a, string actor, string? correlationId) => new()
    {
        // Deterministic from the stored extension: a same-EventId replay
        // republishes the SAME logical event (legacy records without an
        // EventId fall back to a fresh one).
        EventId = a.DeadlineExtension?.EventId is { Length: > 0 } stored ? stored : Guid.NewGuid().ToString(),
        EventType = AppealDeadlineExtendedType,
        EventVersion = EventVersion,
        OccurredAt = a.DeadlineExtension?.ExtendedAt ?? DateTime.UtcNow,
        TenantId = a.TenantId,
        AppealId = a.Id,
        CurrentStatus = (a.DeadlineExtension?.StatusAtExtension ?? a.Status).ToString(),
        LineOfBusiness = a.LineOfBusiness.ToString(),
        Reason = a.DeadlineExtension?.Reason.ToString(),
        ExtensionDays = a.DeadlineExtension?.ExtensionDays,
        PreviousTargetResponseDate = a.DeadlineExtension?.PreviousTargetResponseDate,
        TargetResponseDate = a.DeadlineExtension?.NewTargetResponseDate ?? a.TargetResponseDate,
        WrittenNoticeSentAt = a.DeadlineExtension?.WrittenNoticeSentAt,
        RegulatoryBasis = a.DeadlineExtension?.RegulatoryBasis,
        Actor = actor,
        CorrelationId = correlationId
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}

// ── Event payload records (tested by field-whitelist assertion) ─────────
// Each record defines the COMPLETE wire shape. Adding a field here is a
// deliberate wire-format change; the field-whitelist tests enforce no
// encrypted-at-rest value silently leaks onto the event stream.

public sealed record AppealCreatedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string AppealNumber { get; init; } = string.Empty;
    public string ClaimId { get; init; } = string.Empty;
    public string ClaimNumber { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string ProviderNPI { get; init; } = string.Empty;
    public string AppealType { get; init; } = string.Empty;
    public string AppealLevel { get; init; } = string.Empty;
    public string LineOfBusiness { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTime? TargetResponseDate { get; init; }
    public bool IsUrgent { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealStatusChangedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string FromStatus { get; init; } = string.Empty;
    public string ToStatus { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealClosedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string FromStatus { get; init; } = string.Empty;
    public string? ClosureReasonCode { get; init; }
    public string? DecisionType { get; init; }
    public decimal? ApprovedAmount { get; init; }
    public DateTime? DecisionDate { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealNoteAddedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string NoteId { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public bool IsInternal { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealAttachmentAddedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string AttachmentId { get; init; } = string.Empty;
    public string AttachmentTypeCode { get; init; } = string.Empty;
    public string TransmissionCode { get; init; } = string.Empty;
    public string? ControlNumber { get; init; }
    public DateTime UploadedAt { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealAttachmentAcknowledgedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string AttachmentId { get; init; } = string.Empty;
    public bool AcknowledgmentReceived { get; init; }
    public DateTime? SentDate { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealOverdueObservedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string CurrentStatus { get; init; } = string.Empty;
    public DateTime? TargetResponseDate { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealAssignedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string? AssignedReviewerId { get; init; }
    public string? PreviousReviewerId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealStatusMigratedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string LegacyStatus { get; init; } = string.Empty;
    public string MappedReasonCode { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}

public sealed record AppealDeadlineExtendedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string AppealId { get; init; } = string.Empty;
    public string CurrentStatus { get; init; } = string.Empty;
    public string LineOfBusiness { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public int? ExtensionDays { get; init; }
    public DateTime? PreviousTargetResponseDate { get; init; }
    public DateTime? TargetResponseDate { get; init; }
    public DateTime? WrittenNoticeSentAt { get; init; }
    public string? RegulatoryBasis { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}
