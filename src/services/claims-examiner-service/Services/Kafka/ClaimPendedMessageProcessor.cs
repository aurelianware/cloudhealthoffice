using System.Text.Json;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using CloudHealthOffice.Events.Kafka;
using Microsoft.Extensions.Options;

namespace ClaimsExaminerService.Services.Kafka;

/// <summary>Retry and dead-letter settings of the claims-pended consumer (<c>Examiner:Consumer</c>).</summary>
public sealed class ClaimPendedConsumerOptions
{
    public const string SectionName = "Examiner:Consumer";

    /// <summary>Attempts per message, the first included, before it is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Where a message goes after its last failed attempt; default <c>{topic}.dlq</c>.</summary>
    public string? DeadLetterTopic { get; set; }
}

/// <summary>What became of one message.</summary>
public enum ClaimPendedMessageOutcome
{
    /// <summary>Examined, out of scope, or unreadable: done, commit past it.</summary>
    Processed,

    /// <summary>Failed every attempt; its copy is on the dead-letter topic: commit past it.</summary>
    DeadLettered,
}

/// <summary>
/// Handles one claims.pended.v1 message until it is either processed or on
/// the dead-letter topic. Only then may the consumer commit past it:
/// <list type="bullet">
///   <item>an unreadable message is skipped, since a retry cannot fix it;</item>
///   <item>a transient failure (<see cref="ExaminerTransientException"/>, when
///     claims-service is unreachable) is retried with backoff up to
///     <see cref="ClaimPendedConsumerOptions.MaxAttempts"/>, then dead-lettered;</item>
///   <item>any other failure is dead-lettered at once;</item>
///   <item>a dead-letter publish that fails is retried until it succeeds, so a
///     message is never lost by advancing the offset past it.</item>
/// </list>
/// </summary>
public sealed class ClaimPendedMessageProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly IChoEventProducer _producer;
    private readonly ClaimPendedConsumerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaimPendedMessageProcessor> _logger;

    public ClaimPendedMessageProcessor(
        IServiceScopeFactory scopes, IChoEventProducer producer, IOptions<ClaimPendedConsumerOptions> options,
        ILogger<ClaimPendedMessageProcessor> logger, TimeProvider? clock = null)
    {
        _scopes = scopes;
        _producer = producer;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ClaimPendedMessageOutcome> ProcessAsync(string topic, string? key, string value, CancellationToken ct)
    {
        ClaimPendedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ClaimPendedEvent>(value, JsonOptions);
        }
        catch (JsonException ex)
        {
            // A poison message cannot succeed on retry; alerting on this line is the response.
            _logger.LogError(ex, "Skipping an unreadable ClaimPendedEvent on {Topic}", topic);
            return ClaimPendedMessageOutcome.Processed;
        }
        if (evt is null)
        {
            _logger.LogWarning("Skipping a null ClaimPendedEvent on {Topic}", topic);
            return ClaimPendedMessageOutcome.Processed;
        }

        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
        {
            try
            {
                // Each attempt gets its own DI scope (scoped orchestrator, HttpClient handlers).
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IExaminerOrchestrator>().ProcessAsync(evt, ct);
                return ClaimPendedMessageOutcome.Processed;
            }
            catch (ExaminerTransientException ex)
            {
                last = ex;
                if (attempt >= _options.MaxAttempts) break;
                var delay = Backoff(attempt);
                _logger.LogWarning(ex,
                    "Claim {ClaimId}: attempt {Attempt}/{Max} failed transiently; retrying in {Delay}",
                    evt.ClaimId, attempt, _options.MaxAttempts, delay);
                await Task.Delay(delay, _clock, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                _logger.LogError(ex, "Claim {ClaimId}: unexpected failure; dead-lettering the event", evt.ClaimId);
                break;
            }
        }

        await DeadLetterAsync(topic, key, value, evt, last, ct);
        return ClaimPendedMessageOutcome.DeadLettered;
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt - 1, 20));
        return TimeSpan.FromTicks((long)Math.Min(_options.BaseDelay.Ticks * factor, _options.MaxDelay.Ticks));
    }

    private async Task DeadLetterAsync(string topic, string? key, string value, ClaimPendedEvent evt, Exception? error, CancellationToken ct)
    {
        var deadLetterTopic = string.IsNullOrWhiteSpace(_options.DeadLetterTopic) ? topic + ".dlq" : _options.DeadLetterTopic!;
        var headers = new Dictionary<string, string>
        {
            ["original-topic"] = topic,
            ["tenant-id"] = evt.TenantId ?? string.Empty,
            ["error-type"] = error?.GetType().Name ?? "unknown",
        };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _producer.ProduceAsync(deadLetterTopic, key ?? evt.ClaimId ?? string.Empty, value, headers, ct);
                _logger.LogError(error,
                    "Claim {ClaimId}: AI examination failed every attempt; the event is on {DeadLetterTopic} for replay",
                    evt.ClaimId, deadLetterTopic);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Never advance past a message that is neither processed nor dead-lettered.
                var delay = Backoff(attempt);
                _logger.LogCritical(ex,
                    "Claim {ClaimId}: could not publish to {DeadLetterTopic}; holding the partition and retrying in {Delay}",
                    evt.ClaimId, deadLetterTopic, delay);
                await Task.Delay(delay, _clock, ct);
            }
        }
    }
}
