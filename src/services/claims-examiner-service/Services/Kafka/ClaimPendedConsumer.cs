using Confluent.Kafka;

namespace ClaimsExaminerService.Services.Kafka;

/// <summary>
/// Background consumer for claims.pended.v1. Each message is handed to
/// <see cref="ClaimPendedMessageProcessor"/>, which returns only once it is
/// processed or on the dead-letter topic; only then is the offset committed.
/// A transient failure (claims-service unreachable) is retried, never skipped.
/// If the consume loop itself fails, the failure is logged as critical and
/// rethrown, so the host stops and is restarted instead of running on without
/// a consumer.
/// </summary>
public class ClaimPendedConsumer : BackgroundService
{
    private readonly ClaimPendedMessageProcessor _processor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimPendedConsumer> _logger;

    public ClaimPendedConsumer(
        ClaimPendedMessageProcessor processor,
        IConfiguration configuration,
        ILogger<ClaimPendedConsumer> logger)
    {
        _processor = processor;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield execution back to the host/startup sequence before we touch
        // anything synchronous. Task.Yield() schedules the remainder of this
        // method as a continuation (on the thread pool when no sync context
        // is present, which is the case during generic-host startup), so
        // BackgroundService.StartAsync can finish capturing _executeTask and
        // return Task.CompletedTask — letting the host proceed to start the
        // next hosted service (including Kestrel).
        //
        // Without this yield, the synchronous librdkafka Consume loop below
        // runs on whatever thread the host used to invoke our StartAsync
        // sequentially, blocks it waiting for a message that never arrives,
        // and Kestrel never gets its turn to bind the HTTP port. Symptom is
        // probes failing with "connection refused" and Kestrel.BindAsync
        // eventually throwing TaskCanceledException during host shutdown.
        await Task.Yield();

        var bootstrap = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrap))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — claims-pended consumer disabled");
            return;
        }

        var topic = _configuration["Kafka:ClaimPendedTopic"] ?? "claims.pended.v1";
        var groupId = _configuration["Kafka:ConsumerGroupId"] ?? "claims-examiner-service";

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = groupId,
            ClientId = _configuration["Kafka:ClientId"] ?? "claims-examiner-service",
            // earliest gives us replay on first deploy without losing events
            // that were emitted while the service was being rolled out.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            SessionTimeoutMs = 45_000
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            config.SaslUsername = saslUsername;
            config.SaslPassword = _configuration["Kafka:SaslPassword"];
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        IConsumer<string, string>? consumer = null;
        try
        {
            consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(topic);
            _logger.LogInformation(
                "Claims-pended consumer subscribed to {Topic} (group={Group}, bootstrap={Bootstrap})",
                topic, groupId, bootstrap);

            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    // Poll with a bounded timeout instead of blocking on the
                    // cancellation-token overload. The token overload blocks
                    // the calling thread indefinitely when the topic is empty,
                    // which (a) makes cancellation laggy and (b) caused the
                    // host-startup hang symptom when combined with a missing
                    // await at the top of this method. A 1-second poll keeps
                    // the while loop iterating so cancellation is checked
                    // every tick.
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                    continue;
                }

                if (result?.Message is null)
                {
                    // No message within the poll window — loop back to the
                    // cancellation check. This is the hot path when the topic
                    // is idle.
                    continue;
                }

                // Returns only once the message is processed or dead-lettered.
                await _processor.ProcessAsync(result.Topic, result.Message.Key, result.Message.Value, stoppingToken);

                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException ex)
                {
                    // The message is redelivered after a rebalance; the examination is idempotent.
                    _logger.LogError(ex, "Failed to commit offset for {Topic} {Partition} {Offset}",
                        result.Topic, result.Partition, result.Offset);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Not a silent exit: the host stops (BackgroundServiceExceptionBehavior.StopHost)
            // and the orchestrator restarts the pod with a fresh consumer.
            _logger.LogCritical(ex, "Claims-pended consumer failed; stopping the host so it is restarted");
            throw;
        }
        finally
        {
            // Leaving the group cleanly only speeds up the next rebalance; a failed Close must
            // not replace the exception (if any) that ended the loop.
            try { consumer?.Close(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Closing the claims-pended consumer failed"); }
            consumer?.Dispose();
        }
    }
}
