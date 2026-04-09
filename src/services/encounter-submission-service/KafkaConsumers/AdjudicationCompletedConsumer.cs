using System.Text.Json;
using Confluent.Kafka;
using EncounterSubmissionService.Services;

namespace EncounterSubmissionService.KafkaConsumers;

/// <summary>
/// Kafka consumer that listens to the adjudication-completed topic.
/// When a FL Medicaid claim is adjudicated, automatically creates an
/// <see cref="Models.EncounterSubmission"/> record to track the 60-day
/// FMMIS submission window.
/// </summary>
public class AdjudicationCompletedConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AdjudicationCompletedConsumer> _logger;
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AdjudicationCompletedConsumer(
        IServiceProvider serviceProvider,
        ILogger<AdjudicationCompletedConsumer> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — adjudication consumer disabled");
            return;
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = _configuration["Kafka:AdjudicationGroupId"]
                ?? "encounter-submission-adjudication-consumer",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            consumerConfig.SaslUsername = saslUsername;
            consumerConfig.SaslPassword = _configuration["Kafka:SaslPassword"];
            consumerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            consumerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        var topic = _configuration["Kafka:AdjudicationCompletedTopic"] ?? "adjudication-completed";

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topic);

        _logger.LogInformation("Adjudication consumer subscribed to {Topic}", topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? consumeResult = null;
            try
            {
                consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message?.Value == null) continue;

                var message = JsonSerializer.Deserialize<AdjudicationCompletedMessage>(
                    consumeResult.Message.Value, JsonOptions);

                if (message == null) continue;

                // Only process FL Medicaid claims
                if (!string.Equals(message.StateCode, "FL", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(message.LineOfBusiness, "Medicaid", StringComparison.OrdinalIgnoreCase))
                {
                    consumer.Commit(consumeResult);
                    continue;
                }

                using var scope = _serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IEncounterSubmissionService>();

                // Set tenant context via HttpContextAccessor
                var httpContextAccessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                if (httpContextAccessor.HttpContext == null)
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Items["TenantId"] = message.TenantId;
                    httpContextAccessor.HttpContext = httpContext;
                }
                else
                {
                    httpContextAccessor.HttpContext.Items["TenantId"] = message.TenantId;
                }

                await service.CreateSubmissionRecordAsync(message.ClaimId, message.TenantId, message.AdjudicatedAt);

                consumer.Commit(consumeResult);

                _logger.LogInformation(
                    "Created encounter submission for FL Medicaid claim {ClaimId}, tenant {TenantId}",
                    message.ClaimId, message.TenantId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing adjudication-completed message");
            }
        }

        consumer.Close();
    }
}

/// <summary>
/// Kafka message payload for the adjudication-completed topic.
/// </summary>
public class AdjudicationCompletedMessage
{
    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public DateTime AdjudicatedAt { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
