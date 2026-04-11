using System.Text.Json;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using Confluent.Kafka;

namespace ClaimsExaminerService.Services.Kafka;

/// <summary>
/// Background consumer for claims.pended.v1. Pulls events off the topic and
/// hands each one to ExaminerOrchestrator. Errors are logged and the offset
/// is committed regardless — by design, a single bad event must not block the
/// queue. Genuinely transient errors (network blips talking to Anthropic) are
/// already retried inside the orchestrator's HttpClient and Anthropic SDK retry
/// policies, so a hard failure here means the event is structurally bad.
/// </summary>
public class ClaimPendedConsumer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimPendedConsumer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ClaimPendedConsumer(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<ClaimPendedConsumer> logger)
    {
        _services = services;
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

                await HandleMessageAsync(result, stoppingToken);

                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException ex)
                {
                    _logger.LogError(ex, "Failed to commit offset for {Topic} {Partition} {Offset}",
                        result.Topic, result.Partition, result.Offset);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claims-pended consumer crashed");
        }
        finally
        {
            try { consumer?.Close(); } catch { /* ignore on shutdown */ }
            consumer?.Dispose();
        }
    }

    private async Task HandleMessageAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        ClaimPendedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ClaimPendedEvent>(result.Message.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Malformed event — log and skip. The offset commit in the caller
            // moves us past this poison message; alerting on this log line is
            // the right operational response, not retrying.
            _logger.LogError(ex,
                "Failed to deserialize ClaimPendedEvent at {Topic} {Partition} {Offset}",
                result.Topic, result.Partition, result.Offset);
            return;
        }

        if (evt is null)
        {
            _logger.LogWarning("Null ClaimPendedEvent at {Offset}", result.Offset);
            return;
        }

        // Each message gets its own DI scope so the orchestrator and its
        // collaborators (HttpClient handlers, etc.) get scoped lifetimes.
        using var scope = _services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IExaminerOrchestrator>();

        try
        {
            await orchestrator.ProcessAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled error processing ClaimPendedEvent for claim {ClaimId}; advancing offset",
                evt.ClaimId);
        }
    }
}
