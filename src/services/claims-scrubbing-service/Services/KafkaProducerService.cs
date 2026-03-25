using System.Text;
using System.Text.Json;
using ClaimsScrubbingService.Models;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace ClaimsScrubbingService.Services;

public interface IKafkaProducerService
{
    Task SendAsync(string topic, string key, object value, Dictionary<string, string>? headers = null);
    Task RouteClaimAsync(X12837Claim claim, ClaimValidationResult result);
}

public class KafkaProducerService : IKafkaProducerService, IHostedService, IAsyncDisposable
{
    private readonly ILogger<KafkaProducerService> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    // Topic config keys
    private string CleanClaimsTopic    => _configuration["Kafka:CleanClaimsTopic"]    ?? "claims.clean";
    private string FlaggedClaimsTopic  => _configuration["Kafka:FlaggedClaimsTopic"]  ?? "claims.flagged";
    private string RejectedClaimsTopic => _configuration["Kafka:RejectedClaimsTopic"] ?? "claims.rejected";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public KafkaProducerService(ILogger<KafkaProducerService> logger, IConfiguration configuration)
    {
        _logger        = logger;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — Kafka producer disabled");
            return;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId         = _configuration["Kafka:ClientId"] ?? "claims-scrubbing-service",
            MessageTimeoutMs = 10_000,
            RequestTimeoutMs = 10_000,
            SocketTimeoutMs  = 10_000
        };

        // SASL
        var saslUsername = _configuration["Kafka:SaslUsername"];
        var saslPassword = _configuration["Kafka:SaslPassword"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            producerConfig.SaslUsername     = saslUsername;
            producerConfig.SaslPassword     = saslPassword;
            producerConfig.SaslMechanism    = SaslMechanism.ScramSha512;
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        try
        {
            // Probe connectivity using AdminClient (supports GetMetadata)
            // If broker is unreachable within 10 s the constructor or GetMetadata throws.
            var adminConfig = new AdminClientConfig
            {
                BootstrapServers    = producerConfig.BootstrapServers,
                SocketTimeoutMs     = 10_000,
                SaslUsername        = producerConfig.SaslUsername,
                SaslPassword        = producerConfig.SaslPassword,
                SaslMechanism       = producerConfig.SaslMechanism,
                SecurityProtocol    = producerConfig.SecurityProtocol
            };

            using var adminClient = new AdminClientBuilder(adminConfig).Build();
            adminClient.GetMetadata(TimeSpan.FromSeconds(10)); // throws if unreachable

            _producer  = new ProducerBuilder<string, string>(producerConfig).Build();
            _available = true;
            _logger.LogInformation("Kafka producer connected to {Servers}", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka connection failed — continuing in degraded mode (no routing)");
            _producer?.Dispose();
            _producer  = null;
            _available = false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
        _producer  = null;
        _available = false;
        return Task.CompletedTask;
    }

    public async Task RouteClaimAsync(X12837Claim claim, ClaimValidationResult result)
    {
        if (!_available || _producer == null) return;

        string topic = result.Routing.Destination switch
        {
            "adjudication" => CleanClaimsTopic,
            "work-queue" when result.Routing.QueueName == "claims-errors" => RejectedClaimsTopic,
            "work-queue" => FlaggedClaimsTopic,
            _ => FlaggedClaimsTopic
        };

        var payload = new
        {
            claim,
            validationResult = result,
            timestamp        = DateTime.UtcNow.ToString("O"),
            correlationId    = claim.ClaimId,
            messageId        = Guid.NewGuid().ToString()
        };

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["destination"]  = result.Routing.Destination,
            ["claim-type"]   = claim.ClaimType
        };

        await SendAsync(topic, claim.ClaimId, payload, headers);
    }

    public async Task SendAsync(string topic, string key, object value, Dictionary<string, string>? headers = null)
    {
        if (!_available || _producer == null) return;

        try
        {
            var message = new Message<string, string>
            {
                Key     = key,
                Value   = JsonSerializer.Serialize(value, JsonOptions),
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
