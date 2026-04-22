using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsentService.Models;
using Confluent.Kafka;

namespace ConsentService.Services;

/// <summary>
/// Kafka producer for <c>consent.status-changed.v1</c>. Mirrors the shape
/// of <c>ClaimsService.Services.ClaimEventPublisher</c>:
/// - <see cref="IHostedService"/> + <see cref="IAsyncDisposable"/>.
/// - Degraded mode when <c>Kafka:BootstrapServers</c> is unset — publish
///   becomes a no-op; service still boots. DB is the source of truth.
/// - One topic per event type.
/// - Partition key = <c>consentId</c> (per-consent ordering preserved).
/// - Headers: <c>tenant-id</c>, <c>event-type</c>, <c>event-version</c>.
/// </summary>
public sealed class ConsentEventPublisher : IConsentEventPublisher, IHostedService, IAsyncDisposable
{
    public const string StatusChangedTopic = "consent.status-changed.v1";
    public const string EventTypeName = "ConsentStatusChanged";
    public const string EventVersion = "1.0";

    private readonly ILogger<ConsentEventPublisher> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public ConsentEventPublisher(ILogger<ConsentEventPublisher> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — consent event publisher disabled");
            return Task.CompletedTask;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = _configuration["Kafka:ClientId"] ?? "consent-service",
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
            _logger.LogInformation("Consent event publisher connected to Kafka at {Servers}", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — consent event publisher running in degraded mode");
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
        Consent consent,
        ConsentStatus? fromStatus,
        ConsentStatus toStatus,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (!_available || _producer == null)
        {
            _logger.LogDebug("Kafka producer unavailable; skipping ConsentStatusChanged for consent {ConsentId}", consent.Id);
            return;
        }

        var evt = BuildEvent(consent, fromStatus, toStatus, actor, correlationId);

        var message = new Message<string, string>
        {
            Key = consent.Id,
            Value = JsonSerializer.Serialize(evt, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(consent.TenantId) },
                { "event-type", Encoding.UTF8.GetBytes(EventTypeName) },
                { "event-version", Encoding.UTF8.GetBytes(EventVersion) }
            }
        };

        try
        {
            await _producer.ProduceAsync(StatusChangedTopic, message, ct);
            _logger.LogInformation(
                "Published ConsentStatusChanged for consent {ConsentId} {From}->{To}",
                consent.Id, fromStatus?.ToString() ?? "(none)", toStatus);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish ConsentStatusChanged for consent {ConsentId}: {Reason}",
                consent.Id, ex.Error.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error publishing ConsentStatusChanged for consent {ConsentId}",
                consent.Id);
        }
    }

    /// <summary>
    /// Internal for test visibility. The PHI-adjacent free-text fields
    /// (<c>Reason</c>, <c>GrantedToName</c>, <c>GrantedToContact</c>,
    /// <c>Purpose</c>) are deliberately excluded from the event payload —
    /// they stay encrypted at rest on the consent aggregate, never on the
    /// wire. A field-whitelist test in the test project enforces this.
    /// </summary>
    internal static ConsentStatusChangedEventPayload BuildEvent(
        Consent consent,
        ConsentStatus? fromStatus,
        ConsentStatus toStatus,
        string actor,
        string? correlationId) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = EventTypeName,
        EventVersion = EventVersion,
        OccurredAt = DateTime.UtcNow,
        TenantId = consent.TenantId,
        ConsentId = consent.Id,
        MemberId = consent.MemberId,
        ConsentType = consent.ConsentType.ToString(),
        SensitiveCategory = consent.SensitiveCategory,
        FromStatus = fromStatus?.ToString(),
        ToStatus = toStatus.ToString(),
        EffectiveAt = consent.EffectiveAt,
        ExpiresAt = consent.ExpiresAt,
        Actor = actor,
        CorrelationId = correlationId,
        RevocationReasonCode = consent.RevocationReasonCode?.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Wire payload for <c>consent.status-changed.v1</c>. The shape is tested
/// by field-whitelist assertion (not substring scan) to ensure no
/// PHI-adjacent field can silently leak onto the event stream.
/// </summary>
public sealed record ConsentStatusChangedEventPayload
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string EventVersion { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string ConsentId { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string ConsentType { get; init; } = string.Empty;
    public string? SensitiveCategory { get; init; }
    public string? FromStatus { get; init; }
    public string ToStatus { get; init; } = string.Empty;
    public DateTime? EffectiveAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? RevocationReasonCode { get; init; }
}
