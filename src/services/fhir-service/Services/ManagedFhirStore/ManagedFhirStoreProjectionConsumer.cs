using System.Text.Json;
using CloudHealthOffice.Events;
using Confluent.Kafka;

namespace FhirService.Services.ManagedFhirStore;

/// <summary>
/// Subscribes to <c>claims.finalized.v1</c> and hands each event to
/// <see cref="ManagedFhirStoreProjector"/>. Registered only when a managed
/// FHIR store is configured (ADR 023).
///
/// Its own consumer group, so publishing to the managed store never competes
/// with accumulator-service for messages. Offsets are committed only after a
/// terminal outcome (published, other tenant, not found, rejected); a failure
/// seeks back and retries the same message after a pause, so a claim is never
/// skipped. Matches accumulator-service's ClaimFinalizedConsumer.
/// </summary>
public sealed class ManagedFhirStoreProjectionConsumer : BackgroundService
{
    public const string Topic = "claims.finalized.v1";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ManagedFhirStoreProjector _projector;
    private readonly IConfiguration _config;
    private readonly ILogger<ManagedFhirStoreProjectionConsumer> _logger;

    public ManagedFhirStoreProjectionConsumer(
        ManagedFhirStoreProjector projector,
        IConfiguration config,
        ILogger<ManagedFhirStoreProjectionConsumer> logger)
    {
        _projector = projector;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = _config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured; managed FHIR store publishing is idle");
            return Task.CompletedTask;
        }

        return Task.Run(() => RunLoopAsync(bootstrap, stoppingToken), stoppingToken);
    }

    private async Task RunLoopAsync(string bootstrap, CancellationToken stoppingToken)
    {
        var cfg = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = _config["Kafka:ManagedFhirStore:GroupId"] ?? "fhir-service.managed-fhir-store",
            ClientId = _config["Kafka:ClientId"] ?? "fhir-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false
        };

        var sasl = _config["Kafka:SaslUsername"];
        if (!string.IsNullOrWhiteSpace(sasl))
        {
            cfg.SaslUsername = sasl;
            cfg.SaslPassword = _config["Kafka:SaslPassword"];
            cfg.SaslMechanism = SaslMechanism.ScramSha512;
            cfg.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        using var consumer = new ConsumerBuilder<string, string>(cfg)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka consumer error: {Reason}", e.Reason))
            .Build();

        try
        {
            consumer.Subscribe(Topic);
            _logger.LogInformation("Managed FHIR store publisher subscribed to {Topic}", Topic);

            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Consume error: {Reason}", ex.Error.Reason);
                    continue;
                }
                if (result?.Message is null) continue;

                try
                {
                    await ProcessAsync(result.Message.Value, stoppingToken);
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Publishing to the managed FHIR store failed at {Topic}:{Partition}:{Offset}; the message will be redelivered",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                    try { consumer.Seek(result.TopicPartitionOffset); }
                    catch (KafkaException seekEx) { _logger.LogWarning(seekEx, "Seek back failed; redelivery waits for a rebalance"); }
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            try { consumer.Close(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing the managed FHIR store consumer failed"); }
        }
    }

    private async Task ProcessAsync(string payload, CancellationToken ct)
    {
        ClaimFinalizedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ClaimFinalizedEvent>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring a claims.finalized message that is not valid JSON");
            return;
        }

        if (evt is null || string.IsNullOrWhiteSpace(evt.ClaimId) || string.IsNullOrWhiteSpace(evt.TenantId))
        {
            _logger.LogWarning("Ignoring a claims.finalized message with missing required fields");
            return;
        }

        await _projector.ProjectClaimAsync(evt, ct);
    }
}
