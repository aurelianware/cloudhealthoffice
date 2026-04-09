using System.Text.Json;
using Confluent.Kafka;
using EncounterSubmissionService.Models.Events;
using EncounterSubmissionService.Services;

namespace EncounterSubmissionService.KafkaConsumers;

/// <summary>
/// Kafka consumer that listens to the <c>adjudication-completed</c> topic.
/// When a claim is <b>Approved</b> or <b>Paid</b> AND the tenant has a
/// StateCode configured, automatically creates an <see cref="Models.EncounterSubmission"/>
/// record to track the 60-day FMMIS submission window and publishes an
/// <see cref="EncounterSubmissionCreatedEvent"/>.
///
/// Follows the existing BackgroundService Kafka consumer pattern from
/// <c>authorization-service/Consumers/RfaiDocsReceivedConsumer.cs</c>.
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

    /// <summary>
    /// Claim statuses that qualify for encounter submission tracking.
    /// </summary>
    private static readonly HashSet<string> EligibleStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Approved", "Paid", "PartiallyPaid"
    };

    // Constructor for production use (BackgroundService with DI)
    public AdjudicationCompletedConsumer(
        IServiceProvider serviceProvider,
        ILogger<AdjudicationCompletedConsumer> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    // Constructor for unit testing (direct service injection)
    private readonly IEncounterSubmissionService? _testService;

    internal AdjudicationCompletedConsumer(
        IEncounterSubmissionService service,
        ILogger<AdjudicationCompletedConsumer> logger)
    {
        _serviceProvider = null!;
        _logger = logger;
        _configuration = null!;
        _testService = service;
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

                await ProcessMessageAsync(message, service);

                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Offset not committed — message will be redelivered
                _logger.LogError(ex, "Error processing adjudication-completed message");
            }
        }

        consumer.Close();
    }

    /// <summary>
    /// Process a single adjudication-completed message. Public for unit testing.
    /// </summary>
    public async Task ProcessMessageAsync(AdjudicationCompletedMessage message)
    {
        if (_testService == null)
            throw new InvalidOperationException("Use the overload with service parameter");
        await ProcessMessageAsync(message, _testService);
    }

    internal async Task ProcessMessageAsync(
        AdjudicationCompletedMessage message,
        IEncounterSubmissionService service)
    {
        // Gate 1: claim must be Approved or Paid
        if (!EligibleStatuses.Contains(message.Status))
        {
            _logger.LogDebug(
                "Skipping claim {ClaimId}: status '{Status}' not eligible for encounter tracking",
                message.ClaimId, message.Status);
            return;
        }

        // Gate 2: tenant must have a StateCode set (FL Medicaid encounter requirement)
        if (string.IsNullOrWhiteSpace(message.StateCode))
        {
            _logger.LogDebug(
                "Skipping claim {ClaimId}: tenant {TenantId} has no StateCode configured",
                message.ClaimId, message.TenantId);
            return;
        }

        // Gate 3: only FL Medicaid claims require FMMIS encounter submission
        if (!string.Equals(message.StateCode, "FL", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(message.LineOfBusiness, "Medicaid", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping claim {ClaimId}: state '{State}' / LOB '{Lob}' not FL Medicaid",
                message.ClaimId, message.StateCode, message.LineOfBusiness);
            return;
        }

        var submission = await service.CreateSubmissionRecordAsync(
            message.ClaimId, message.TenantId, message.AdjudicatedAt);

        // Publish EncounterSubmissionCreatedEvent
        await PublishCreatedEventAsync(submission);

        _logger.LogInformation(
            "Created encounter submission {SubmissionId} for FL Medicaid claim {ClaimId} " +
            "(status={Status}), tenant {TenantId}, deadline {Deadline:yyyy-MM-dd}",
            submission.Id, message.ClaimId, message.Status,
            message.TenantId, submission.SubmissionDeadline);
    }

    private async Task PublishCreatedEventAsync(Models.EncounterSubmission submission)
    {
        var bootstrapServers = _configuration?["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers)) return;

        var topic = _configuration?["Kafka:SubmissionCreatedTopic"] ?? "encounter-submission-created";

        var producerConfig = new ProducerConfig { BootstrapServers = bootstrapServers };

        var saslUsername = _configuration?["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            producerConfig.SaslUsername = saslUsername;
            producerConfig.SaslPassword = _configuration?["Kafka:SaslPassword"];
            producerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        try
        {
            using var producer = new ProducerBuilder<string, string>(producerConfig).Build();

            var payload = JsonSerializer.Serialize(new EncounterSubmissionCreatedEvent
            {
                SubmissionId = submission.Id,
                ClaimId = submission.ClaimId,
                TenantId = submission.TenantId,
                Deadline = submission.SubmissionDeadline
            }, JsonOptions);

            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = submission.TenantId,
                Value = payload
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish EncounterSubmissionCreatedEvent for {SubmissionId}",
                submission.Id);
        }
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
