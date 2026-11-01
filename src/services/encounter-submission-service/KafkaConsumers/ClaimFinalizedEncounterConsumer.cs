using System.Text.Json;
using CloudHealthOffice.Events;
using Confluent.Kafka;
using EncounterSubmissionService.StateRoutes;

namespace EncounterSubmissionService.KafkaConsumers;

/// <summary>
/// Consumes <c>claims.finalized.v1</c>, the event claims-service actually publishes
/// when a claim is paid, denied or reversed, and turns it into Texas / California
/// encounter work: originals, replacements (from claim adjustments) and voids.
///
/// <para>The older <see cref="AdjudicationCompletedConsumer"/> listens on
/// <c>adjudication-completed</c>, which no service in the repository produces; it is
/// kept for Florida unchanged. This consumer only acts for tenants configured on a
/// state X12 route, so it changes nothing until a route is turned on.</para>
///
/// <para>The offset is committed only after the event is handled; a failure (for
/// example claims-service unavailable) leaves it to be redelivered, and handling is
/// idempotent because encounter record ids are deterministic.</para>
/// </summary>
public sealed class ClaimFinalizedEncounterConsumer : BackgroundService
{
    public const string DefaultTopic = "claims.finalized.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimFinalizedEncounterConsumer> _logger;

    public ClaimFinalizedEncounterConsumer(
        IServiceProvider services, IConfiguration configuration, ILogger<ClaimFinalizedEncounterConsumer> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume blocks; yield first so host startup completes (see AdjudicationCompletedConsumer).
        await Task.Yield();

        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — claims.finalized encounter consumer disabled");
            return;
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = _configuration["Kafka:ClaimFinalized:GroupId"] ?? "encounter-submission-service.claims-finalized",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            config.SaslUsername = saslUsername;
            config.SaslPassword = _configuration["Kafka:SaslPassword"];
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        var topic = _configuration["Kafka:ClaimFinalized:Topic"] ?? DefaultTopic;
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        _logger.LogInformation("Encounter consumer subscribed to {Topic}", topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value is null) continue;

                var evt = JsonSerializer.Deserialize<ClaimFinalizedEvent>(result.Message.Value, JsonOptions);
                if (evt is not null)
                {
                    using var scope = _services.CreateScope();
                    var lifecycle = scope.ServiceProvider.GetRequiredService<IEncounterLifecycleService>();
                    var outcome = await lifecycle.HandleFinalizedAsync(evt, stoppingToken);
                    if (outcome.Action != EncounterLifecycleAction.Ignored)
                        _logger.LogInformation("claims.finalized {EventId}: {Action} {SubmissionId}",
                            evt.EventId, outcome.Action, outcome.SubmissionId);
                }

                consumer.Commit(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException ex)
            {
                // A payload that can never parse would block the partition forever.
                _logger.LogError(ex, "Skipping unreadable claims.finalized message at {Offset}", result?.TopicPartitionOffset);
                if (result is not null) consumer.Commit(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling claims.finalized message; it will be redelivered");
                if (result is not null)
                {
                    try { consumer.Seek(result.TopicPartitionOffset); }
                    catch (KafkaException seekEx) { _logger.LogWarning(seekEx, "Seek after failure did not succeed"); }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        consumer.Close();
    }
}
