using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppealsService.Models;
using Confluent.Kafka;

namespace AppealsService.Services;

/// <summary>
/// Kafka producer for <c>appeal.status-changed.v1</c>. Mirrors the shape
/// of <c>ConsentService.Services.ConsentEventPublisher</c> and
/// <c>PersonalRepresentativeService.Services.PersonalRepEventPublisher</c>:
/// - <see cref="IHostedService"/> + <see cref="IAsyncDisposable"/>.
/// - Degraded mode when <c>Kafka:BootstrapServers</c> is unset — publish
///   becomes a no-op; service still boots. DB is the source of truth.
/// - Single topic, nine event types distinguished by the <c>event-type</c>
///   header.
/// - Partition key = <c>appealId</c> (per-appeal ordering preserved).
/// - Headers: <c>tenant-id</c>, <c>event-type</c>, <c>event-version</c>.
/// </summary>
public sealed class AppealEventPublisher : IAppealEventPublisher, IHostedService, IAsyncDisposable
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

    private readonly ILogger<AppealEventPublisher> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
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
            _logger.LogWarning("Kafka:BootstrapServers not configured — appeal event publisher disabled");
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

        try
        {
            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            _available = true;
            _logger.LogInformation("Appeal event publisher connected to Kafka at {Servers}",
                LogSanitizer.SafeForLog(bootstrapServers));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — appeal event publisher running in degraded mode");
            _producer?.Dispose();
            _producer = null;
            _available = false;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
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
        _available = false;
        return Task.CompletedTask;
    }

    public Task PublishCreatedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildCreatedPayload(appeal, actor, correlationId);
        return ProduceAsync(appeal, AppealCreatedType, evt, ct);
    }

    public Task PublishStatusChangedAsync(
        Appeal appeal, AppealStatus fromStatus, AppealStatus toStatus,
        string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildStatusChangedPayload(appeal, fromStatus, toStatus, actor, correlationId);
        return ProduceAsync(appeal, AppealStatusChangedType, evt, ct);
    }

    public Task PublishClosedAsync(
        Appeal appeal, AppealStatus fromStatus, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildClosedPayload(appeal, fromStatus, actor, correlationId);
        return ProduceAsync(appeal, AppealClosedType, evt, ct);
    }

    public Task PublishNoteAddedAsync(
        Appeal appeal, AppealNote note, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildNoteAddedPayload(appeal, note, actor, correlationId);
        return ProduceAsync(appeal, AppealNoteAddedType, evt, ct);
    }

    public Task PublishAttachmentAddedAsync(
        Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildAttachmentAddedPayload(appeal, attachment, actor, correlationId);
        return ProduceAsync(appeal, AppealAttachmentAddedType, evt, ct);
    }

    public Task PublishAttachmentAcknowledgedAsync(
        Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildAttachmentAcknowledgedPayload(appeal, attachment, actor, correlationId);
        return ProduceAsync(appeal, AppealAttachmentAcknowledgedType, evt, ct);
    }

    public Task PublishOverdueObservedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildOverdueObservedPayload(appeal, actor, correlationId);
        return ProduceAsync(appeal, AppealOverdueObservedType, evt, ct);
    }

    public Task PublishAssignedAsync(
        Appeal appeal, string? previousReviewerId, string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildAssignedPayload(appeal, previousReviewerId, actor, correlationId);
        return ProduceAsync(appeal, AppealAssignedType, evt, ct);
    }

    public Task PublishStatusMigratedAsync(
        Appeal appeal, string legacyStatus, AppealClosureReasonCode mappedReasonCode,
        string actor, string? correlationId, CancellationToken ct = default)
    {
        var evt = BuildStatusMigratedPayload(appeal, legacyStatus, mappedReasonCode, actor, correlationId);
        return ProduceAsync(appeal, AppealStatusMigratedType, evt, ct);
    }

    private async Task ProduceAsync<TPayload>(Appeal appeal, string eventType, TPayload payload, CancellationToken ct)
    {
        if (!_available || _producer == null)
        {
            _logger.LogDebug("Kafka producer unavailable; skipping {EventType} for appeal {AppealId}",
                LogSanitizer.SafeForLog(eventType), LogSanitizer.SafeForLog(appeal.Id));
            return;
        }

        var message = new Message<string, string>
        {
            Key = appeal.Id,
            Value = JsonSerializer.Serialize(payload, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(appeal.TenantId) },
                { "event-type", Encoding.UTF8.GetBytes(eventType) },
                { "event-version", Encoding.UTF8.GetBytes(EventVersion) }
            }
        };

        try
        {
            await _producer.ProduceAsync(StatusChangedTopic, message, ct);
            _logger.LogInformation("Published {EventType} for appeal {AppealId}",
                LogSanitizer.SafeForLog(eventType), LogSanitizer.SafeForLog(appeal.Id));
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} for appeal {AppealId}: {Reason}",
                LogSanitizer.SafeForLog(eventType), LogSanitizer.SafeForLog(appeal.Id),
                LogSanitizer.SafeForLog(ex.Error.Reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing {EventType} for appeal {AppealId}",
                LogSanitizer.SafeForLog(eventType), LogSanitizer.SafeForLog(appeal.Id));
        }
    }

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
        Appeal a, AppealNote n, string actor, string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealNoteAddedType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
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
