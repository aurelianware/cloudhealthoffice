using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalRepresentativeService.Models;
using Confluent.Kafka;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Kafka producer for <c>personal-rep.status-changed.v1</c>. Mirrors the
/// shape of <c>ConsentService.Services.ConsentEventPublisher</c>:
/// - <see cref="IHostedService"/> + <see cref="IAsyncDisposable"/>.
/// - Degraded mode when <c>Kafka:BootstrapServers</c> is unset — publish
///   becomes a no-op; service still boots. DB is the source of truth.
/// - One topic per aggregate; association events ride the same topic with
///   a different <c>event-type</c> header.
/// - Partition key = <c>personalRepId</c> (per-rep ordering preserved).
/// - Headers: <c>tenant-id</c>, <c>event-type</c>, <c>event-version</c>.
/// </summary>
public sealed class PersonalRepEventPublisher : IPersonalRepEventPublisher, IHostedService, IAsyncDisposable
{
    public const string StatusChangedTopic = "personal-rep.status-changed.v1";
    public const string StatusChangedEventType = "PersonalRepStatusChanged";
    public const string AssociationAddedEventType = "PersonalRepAssociationAdded";
    public const string AssociationRemovedEventType = "PersonalRepAssociationRemoved";
    public const string EventVersion = "1.0";

    private readonly ILogger<PersonalRepEventPublisher> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public PersonalRepEventPublisher(ILogger<PersonalRepEventPublisher> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — Personal Rep event publisher disabled");
            return Task.CompletedTask;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = _configuration["Kafka:ClientId"] ?? "personal-representative-service",
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
            _logger.LogInformation("Personal Rep event publisher connected to Kafka at {Servers}", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — Personal Rep event publisher running in degraded mode");
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

    public async Task PublishStatusChangedAsync(
        PersonalRepresentative rep,
        PersonalRepStatus? fromStatus,
        PersonalRepStatus toStatus,
        IReadOnlyList<string> associatedMemberIds,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (!_available || _producer == null)
        {
            _logger.LogDebug(
                "Kafka producer unavailable; skipping PersonalRepStatusChanged for rep {PersonalRepId}",
                LogSanitizer.SafeForLog(rep.Id));
            return;
        }

        var evt = BuildStatusChangedEvent(rep, fromStatus, toStatus, associatedMemberIds, actor, correlationId);

        var message = new Message<string, string>
        {
            Key = rep.Id,
            Value = JsonSerializer.Serialize(evt, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(rep.TenantId) },
                { "event-type", Encoding.UTF8.GetBytes(StatusChangedEventType) },
                { "event-version", Encoding.UTF8.GetBytes(EventVersion) }
            }
        };

        try
        {
            await _producer.ProduceAsync(StatusChangedTopic, message, ct);
            _logger.LogInformation(
                "Published PersonalRepStatusChanged for rep {PersonalRepId} {From}->{To}",
                LogSanitizer.SafeForLog(rep.Id),
                fromStatus?.ToString() ?? "(none)", toStatus);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish PersonalRepStatusChanged for rep {PersonalRepId}: {Reason}",
                LogSanitizer.SafeForLog(rep.Id),
                LogSanitizer.SafeForLog(ex.Error.Reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error publishing PersonalRepStatusChanged for rep {PersonalRepId}",
                LogSanitizer.SafeForLog(rep.Id));
        }
    }

    public async Task PublishAssociationChangedAsync(
        PersonalRepresentative rep,
        PersonalRepAssociation association,
        PersonalRepEventType eventType,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (!_available || _producer == null)
        {
            _logger.LogDebug(
                "Kafka producer unavailable; skipping {EventType} for rep {PersonalRepId} member {MemberId}",
                eventType,
                LogSanitizer.SafeForLog(rep.Id),
                LogSanitizer.SafeForLog(association.MemberId));
            return;
        }

        var eventTypeName = eventType switch
        {
            PersonalRepEventType.PersonalRepAssociationAdded => AssociationAddedEventType,
            PersonalRepEventType.PersonalRepAssociationRemoved => AssociationRemovedEventType,
            _ => throw new ArgumentException(
                $"PublishAssociationChangedAsync does not accept {eventType}", nameof(eventType))
        };

        var evt = BuildAssociationChangedEvent(rep, association, eventTypeName, actor, correlationId);

        var message = new Message<string, string>
        {
            Key = rep.Id,
            Value = JsonSerializer.Serialize(evt, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(rep.TenantId) },
                { "event-type", Encoding.UTF8.GetBytes(eventTypeName) },
                { "event-version", Encoding.UTF8.GetBytes(EventVersion) }
            }
        };

        try
        {
            await _producer.ProduceAsync(StatusChangedTopic, message, ct);
            _logger.LogInformation(
                "Published {EventType} for rep {PersonalRepId} member {MemberId}",
                eventTypeName,
                LogSanitizer.SafeForLog(rep.Id),
                LogSanitizer.SafeForLog(association.MemberId));
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish {EventType} for rep {PersonalRepId}: {Reason}",
                eventTypeName,
                LogSanitizer.SafeForLog(rep.Id),
                LogSanitizer.SafeForLog(ex.Error.Reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error publishing {EventType} for rep {PersonalRepId}",
                eventTypeName,
                LogSanitizer.SafeForLog(rep.Id));
        }
    }

    /// <summary>
    /// Internal for test visibility. The PHI-adjacent free-text fields
    /// (names, contact, address, notes) are deliberately excluded from the
    /// event payload — they stay encrypted at rest on the rep aggregate,
    /// never on the wire. A field-whitelist test enforces this.
    /// </summary>
    internal static PersonalRepStatusChangedEventPayload BuildStatusChangedEvent(
        PersonalRepresentative rep,
        PersonalRepStatus? fromStatus,
        PersonalRepStatus toStatus,
        IReadOnlyList<string> associatedMemberIds,
        string actor,
        string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = StatusChangedEventType,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = rep.TenantId,
        PersonalRepId = rep.Id,
        CredentialType = rep.CredentialType.ToString(),
        FromStatus = fromStatus?.ToString(),
        ToStatus = toStatus.ToString(),
        EffectiveFrom = rep.EffectiveFrom,
        EffectiveTo = rep.EffectiveTo,
        ExpiresAt = rep.ExpiresAt,
        AssociatedMemberIds = associatedMemberIds.ToList(),
        Actor = actor,
        CorrelationId = correlationId,
        InactivationReasonCode = rep.InactivationReasonCode?.ToString()
    };

    internal static PersonalRepAssociationChangedEventPayload BuildAssociationChangedEvent(
        PersonalRepresentative rep,
        PersonalRepAssociation association,
        string eventTypeName,
        string actor,
        string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = eventTypeName,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = rep.TenantId,
        PersonalRepId = rep.Id,
        MemberId = association.MemberId,
        CredentialType = association.CredentialType.ToString(),
        EffectiveFrom = association.EffectiveFrom,
        EffectiveTo = association.EffectiveTo,
        Actor = actor,
        CorrelationId = correlationId
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Wire payload for <c>PersonalRepStatusChanged</c>. Field-whitelist tested
/// so no PHI-adjacent field can silently leak onto the event stream.
/// </summary>
public sealed record PersonalRepStatusChangedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string PersonalRepId { get; init; } = string.Empty;
    public string CredentialType { get; init; } = string.Empty;
    public string? FromStatus { get; init; }
    public string ToStatus { get; init; } = string.Empty;
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public List<string> AssociatedMemberIds { get; init; } = new();
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? InactivationReasonCode { get; init; }
}

/// <summary>
/// Wire payload for <c>PersonalRepAssociationAdded</c> /
/// <c>PersonalRepAssociationRemoved</c>. Same field-whitelist test
/// treatment — no encrypted rep fields.
/// </summary>
public sealed record PersonalRepAssociationChangedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string PersonalRepId { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string CredentialType { get; init; } = string.Empty;
    public DateTime EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
}
