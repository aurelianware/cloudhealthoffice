using System.Text;
using System.Text.Json;
using CloudHealthOffice.Events;
using Confluent.Kafka;

namespace AccumulatorService.Services;

public class KafkaAccumulatorEventPublisher : IAccumulatorEventPublisher, IHostedService, IAsyncDisposable
{
    public const string AdjustedTopic = "accumulators.adjusted.v1";
    public const string OrphanTopic = "accumulators.orphan.v1";

    private readonly ILogger<KafkaAccumulatorEventPublisher> _logger;
    private readonly IConfiguration _config;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public KafkaAccumulatorEventPublisher(ILogger<KafkaAccumulatorEventPublisher> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrap = _config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — accumulator publisher disabled");
            return Task.CompletedTask;
        }

        var cfg = new ProducerConfig
        {
            BootstrapServers = bootstrap,
            ClientId = _config["Kafka:ClientId"] ?? "accumulator-service",
            MessageTimeoutMs = 10_000,
            RequestTimeoutMs = 10_000,
            EnableIdempotence = true
        };

        var sasl = _config["Kafka:SaslUsername"];
        if (!string.IsNullOrWhiteSpace(sasl))
        {
            cfg.SaslUsername = sasl;
            cfg.SaslPassword = _config["Kafka:SaslPassword"];
            cfg.SaslMechanism = SaslMechanism.ScramSha512;
            cfg.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        try
        {
            _producer = new ProducerBuilder<string, string>(cfg).Build();
            _available = true;
            _logger.LogInformation("Accumulator event publisher connected to Kafka at {Servers}", bootstrap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — accumulator event publisher running in degraded mode");
            _available = false;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _producer?.Flush(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error flushing producer on shutdown"); }
        _producer?.Dispose();
        _producer = null;
        _available = false;
        return Task.CompletedTask;
    }

    public Task PublishAdjustedAsync(AccumulatorAdjustedEvent evt, CancellationToken ct = default)
        => PublishAsync(AdjustedTopic, $"{evt.TenantId}:{evt.MemberId}", evt, evt.EventType, evt.EventSchemaVersion, evt.TenantId, ct);

    public Task PublishOrphanAsync(OrphanAccumulatorClaimEvent evt, CancellationToken ct = default)
        => PublishAsync(OrphanTopic, $"{evt.TenantId}:{evt.ClaimId}", evt, evt.EventType, evt.EventSchemaVersion, evt.TenantId, ct);

    private async Task PublishAsync<T>(string topic, string key, T payload, string eventType, int schemaVersion, string tenantId, CancellationToken ct)
    {
        if (!_available || _producer is null)
        {
            _logger.LogDebug("Kafka unavailable; skipping {EventType} publish", eventType);
            return;
        }

        var message = new Message<string, string>
        {
            Key = key,
            Value = JsonSerializer.Serialize(payload, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(tenantId) },
                { "event-type", Encoding.UTF8.GetBytes(eventType) },
                { "event-schema-version", Encoding.UTF8.GetBytes(schemaVersion.ToString()) }
            }
        };

        try
        {
            await _producer.ProduceAsync(topic, message, ct);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} to {Topic}: {Reason}", eventType, topic, ex.Error.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing {EventType}", eventType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
