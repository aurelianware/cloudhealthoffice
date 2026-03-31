using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using Confluent.Kafka;

namespace AuthorizationService.Consumers;

public class RfaiDocsReceivedConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RfaiDocsReceivedConsumer> _logger;
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Constructor for production use (BackgroundService with DI)
    public RfaiDocsReceivedConsumer(
        IServiceProvider serviceProvider,
        ILogger<RfaiDocsReceivedConsumer> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    // Constructor for unit testing (direct repository injection)
    internal RfaiDocsReceivedConsumer(
        IAuthorizationRepository repository,
        ILogger<RfaiDocsReceivedConsumer> logger)
    {
        _serviceProvider = null!;
        _logger = logger;
        _configuration = null!;
        _testRepository = repository;
    }

    private readonly IAuthorizationRepository? _testRepository;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — RFAI docs consumer disabled");
            return;
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = _configuration["Kafka:RfaiDocsReceivedGroupId"] ?? "authorization-service-rfai-consumer",
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

        var topic = _configuration["Kafka:RfaiDocsReceivedTopic"] ?? "rfai-docs-received";

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topic);

        _logger.LogInformation("RFAI docs consumer subscribed to {Topic}", topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? consumeResult = null;
            try
            {
                consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message?.Value == null) continue;

                var message = JsonSerializer.Deserialize<RfaiDocsReceivedMessage>(
                    consumeResult.Message.Value, JsonOptions);

                if (message == null) continue;

                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();

                // Set tenant context via HttpContextAccessor for repository
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

                await ProcessMessageAsync(message, repository);

                // Commit only after successful processing
                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Offset not committed — message will be redelivered
                _logger.LogError(ex, "Error processing RFAI docs received message");
            }
        }

        consumer.Close();
    }

    /// <summary>
    /// Process a single message. Public for unit testing.
    /// </summary>
    public async Task ProcessMessageAsync(RfaiDocsReceivedMessage message)
    {
        if (_testRepository == null)
            throw new InvalidOperationException("Use the overload with repository parameter");

        await ProcessMessageAsync(message, _testRepository);
    }

    internal async Task ProcessMessageAsync(RfaiDocsReceivedMessage message, IAuthorizationRepository repository)
    {
        var auth = await repository.GetByAuthorizationNumberAsync(message.AuthNumber);

        if (auth == null)
        {
            _logger.LogWarning(
                "Authorization not found for auth number {AuthNumber} from RFAI {RfaiCaseId}",
                message.AuthNumber, message.RfaiCaseId);
            return;
        }

        // Skip if auth is already in a terminal state
        if (auth.Status is AuthorizationStatus.Approved
            or AuthorizationStatus.Modified
            or AuthorizationStatus.Denied
            or AuthorizationStatus.Expired
            or AuthorizationStatus.Cancelled)
        {
            _logger.LogInformation(
                "Authorization {AuthNumber} already in terminal status {Status}, skipping update",
                message.AuthNumber, auth.Status);
            return;
        }

        if (message.AllRequestedItemsReceived)
        {
            auth.Status = AuthorizationStatus.InReview;
            auth.SlaResumedAt = message.ReceivedAt;
            auth.LastUpdatedDate = DateTime.UtcNow;

            await repository.UpdateAsync(auth);

            _logger.LogInformation(
                "SLA clock restarted for authorization {AuthNumber}, RFAI {RfaiCaseId}",
                message.AuthNumber, message.RfaiCaseId);
        }
        else
        {
            _logger.LogInformation(
                "Partial docs received for authorization {AuthNumber}, still awaiting items",
                message.AuthNumber);
        }
    }
}
