using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace RfaiService.Services;

public interface IKafkaProducerService
{
    Task SendAsync(string topic, string key, object value, Dictionary<string, string>? headers = null);
}

public class KafkaProducerService : IKafkaProducerService, IHostedService, IAsyncDisposable
{
    private readonly ILogger<KafkaProducerService> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public KafkaProducerService(ILogger<KafkaProducerService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — Kafka producer disabled");
            return Task.CompletedTask;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = _configuration["Kafka:ClientId"] ?? "rfai-service",
            MessageTimeoutMs = 10_000,
            RequestTimeoutMs = 10_000,
            SocketTimeoutMs = 10_000
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
            using var adminClient = new AdminClientBuilder(
                new AdminClientConfig
                {
                    BootstrapServers = producerConfig.BootstrapServers,
                    SocketTimeoutMs = 10_000,
                    SaslUsername = producerConfig.SaslUsername,
                    SaslPassword = producerConfig.SaslPassword,
                    SaslMechanism = producerConfig.SaslMechanism,
                    SecurityProtocol = producerConfig.SecurityProtocol
                }).Build();
            adminClient.GetMetadata(TimeSpan.FromSeconds(10));

            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            _available = true;
            _logger.LogInformation("Kafka producer connected to {Servers}", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka connection failed — continuing in degraded mode");
            _producer?.Dispose();
            _producer = null;
            _available = false;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
        _producer = null;
        _available = false;
        return Task.CompletedTask;
    }

    public async Task SendAsync(string topic, string key, object value, Dictionary<string, string>? headers = null)
    {
        if (!_available || _producer == null) return;

        try
        {
            var message = new Message<string, string>
            {
                Key = key,
                Value = JsonSerializer.Serialize(value, JsonOptions),
                Headers = new Headers()
            };

            if (headers != null)
            {
                foreach (var (k, v) in headers)
                    message.Headers.Add(k, Encoding.UTF8.GetBytes(v));
            }

            await _producer.ProduceAsync(topic, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to Kafka topic {Topic}", topic);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
