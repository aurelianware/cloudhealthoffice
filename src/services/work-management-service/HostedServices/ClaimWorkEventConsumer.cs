using System.Text.Json;
using CloudHealthOffice.Events;
using Confluent.Kafka;
using WorkManagementService.Services;

namespace WorkManagementService.HostedServices;

/// <summary>
/// Subscriber for claims.work.v1 (the same Kafka wiring as accumulator-service's
/// ClaimFinalizedConsumer): EnableAutoCommit=false, the offset is committed
/// only after the event was applied; a failure seeks back to the message and
/// retries it after a pause, so an event is never skipped. Applying is
/// idempotent (SourceVersion), so a redelivery after a crash between apply and
/// commit is a no-op.
///
/// Disabled when Kafka:BootstrapServers is unset; the periodic reconcile then
/// keeps work items in step (see <see cref="WorkMaintenanceService"/>).
/// </summary>
public sealed class ClaimWorkEventConsumer : BackgroundService
{
    public const string Topic = ClaimWorkEventTopics.Topic;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaimWorkEventConsumer> _logger;

    public ClaimWorkEventConsumer(IServiceProvider services, IConfiguration config, ILogger<ClaimWorkEventConsumer> logger)
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
            _logger.LogWarning("Kafka:BootstrapServers not configured — claims.work.v1 consumer disabled; the periodic reconcile keeps work in step");
            return Task.CompletedTask;
        }
        return Task.Run(() => RunLoop(bootstrap!, stoppingToken), stoppingToken);
    }

    private void RunLoop(string bootstrap, CancellationToken stoppingToken)
    {
        var cfg = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = _config["Kafka:ClaimWork:GroupId"] ?? "work-management-service.claims-work",
            ClientId = _config["Kafka:ClientId"] ?? "work-management-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false,
            // claims-service may not have produced yet; the broker auto-creates
            // the topic (demo) or infra provisions it.
            AllowAutoCreateTopics = true,
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
            _logger.LogInformation("claims.work.v1 consumer subscribed to {Topic}", Topic);
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to apply claim work event at {Topic}:{Partition}:{Offset} — it will be redelivered",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                    try { consumer.Seek(result.TopicPartitionOffset); }
                    catch (KafkaException seekEx) { _logger.LogWarning(seekEx, "Seek back failed; redelivery waits for a rebalance"); }
                    stoppingToken.WaitHandle.WaitOne(RetryDelay);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            // Leaving the group cleanly only speeds up the next rebalance; uncommitted
            // offsets are redelivered either way, so a failed Close must not fail shutdown.
            try { consumer.Close(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing the claim work event consumer failed"); }
        }
    }

    /// <summary>Deserializes and applies one message (internal for tests).</summary>
    internal async Task ProcessAsync(string payload, CancellationToken ct)
    {
        ClaimWorkEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ClaimWorkEvent>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            // A poison message would block the partition forever; it cannot
            // succeed on retry. Reconcile repairs whatever it meant.
            _logger.LogError(ex, "Skipping an unreadable claims.work.v1 message");
            return;
        }
        if (evt is null || string.IsNullOrWhiteSpace(evt.TenantId) || string.IsNullOrWhiteSpace(evt.ClaimId))
        {
            _logger.LogWarning("Skipping a claims.work.v1 message without tenant or claim id");
            return;
        }

        using var scope = _services.CreateScope();
        var intake = scope.ServiceProvider.GetRequiredService<WorkIntakeService>();
        var outcome = await intake.ApplyClaimEventAsync(evt, ct);
        _logger.LogInformation("Applied {EventType} for claim {ClaimId} (tenant {TenantId}): {Outcome}",
            evt.EventType, evt.ClaimId, evt.TenantId, outcome);
    }
}
