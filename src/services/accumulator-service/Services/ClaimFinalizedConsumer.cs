using System.Text.Json;
using CloudHealthOffice.Events;
using Confluent.Kafka;

namespace AccumulatorService.Services;

/// <summary>
/// BackgroundService subscriber for claims.finalized.v1. For each message:
///   - deserialize → ClaimFinalizedEvent
///   - delegate to IAccumulatorService.ApplyClaimFinalizedAsync (idempotent)
///   - commit offset only on a terminal outcome (Applied | Duplicate | Orphan)
///
/// Commit strategy is EnableAutoCommit=false with explicit StoreOffset so
/// transient failures (DB hiccup) get a re-delivery rather than a silent skip.
///
/// TODO(addendum-a): this Kafka subscriber may migrate to the Service Bus-backed
/// IMessageBus at the Phase 1/2 boundary. Current Kafka wiring matches claims-service
/// for consistency; migration decision is explicitly out of scope for this PR.
/// </summary>
public class ClaimFinalizedConsumer : BackgroundService
{
    public const string Topic = "claims.finalized.v1";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaimFinalizedConsumer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaimFinalizedConsumer(IServiceProvider services, IConfiguration config, ILogger<ClaimFinalizedConsumer> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = _config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — ClaimFinalized consumer disabled");
            return Task.CompletedTask;
        }

        // Run on a background thread so the synchronous Consume loop doesn't block
        // the host startup path.
        return Task.Run(() => RunLoop(bootstrap!, stoppingToken), stoppingToken);
    }

    private void RunLoop(string bootstrap, CancellationToken stoppingToken)
    {
        var cfg = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = _config["Kafka:ClaimFinalized:GroupId"] ?? "accumulator-service.claims-finalized",
            ClientId = _config["Kafka:ClientId"] ?? "accumulator-service",
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
            _logger.LogInformation("ClaimFinalized consumer subscribed to {Topic}", Topic);

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
                    ProcessAsync(result.Message.Value, stoppingToken).GetAwaiter().GetResult();
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process ClaimFinalizedEvent at {Topic}:{Partition}:{Offset} — message will be redelivered",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                    // No offset commit → Kafka redelivers on next poll/assignment.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            try { consumer.Close(); } catch { /* best-effort */ }
        }
    }

    private async Task ProcessAsync(string payload, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize<ClaimFinalizedEvent>(payload, JsonOptions);
        if (evt is null || string.IsNullOrWhiteSpace(evt.ClaimId) || string.IsNullOrWhiteSpace(evt.TenantId))
        {
            _logger.LogWarning("Ignoring ClaimFinalized message with missing required fields");
            return;
        }

        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAccumulatorService>();
        var outcome = await svc.ApplyClaimFinalizedAsync(evt, ct);
        _logger.LogInformation(
            "ClaimFinalized processed: claim={ClaimId} tenant={TenantId} outcome={Outcome}",
            evt.ClaimId, evt.TenantId, outcome.Outcome);
    }
}
