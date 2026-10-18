using System.Text;
using Confluent.Kafka;

namespace CloudHealthOffice.Events.Kafka;

/// <summary>Connection settings for <see cref="ChoKafkaProducer"/> (bound by each service from <c>Kafka:*</c>).</summary>
public sealed record ChoKafkaProducerSettings
{
    public string? BootstrapServers { get; init; }
    public string ClientId { get; init; } = "cho";
    public string? SaslUsername { get; init; }
    public string? SaslPassword { get; init; }

    /// <summary>How long one produce may wait for the broker's acknowledgement.</summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Publishes one event and waits for the broker's acknowledgement; throws when it is not acknowledged.</summary>
public interface IChoEventProducer
{
    /// <summary>False when Kafka is not configured: nothing can be published.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Produces <paramref name="value"/> to <paramref name="topic"/> under
    /// <paramref name="key"/> and returns once every in-sync replica has it
    /// (<c>acks=all</c>). Throws <see cref="ProduceException{TKey,TValue}"/>
    /// (or <see cref="InvalidOperationException"/> when not configured) when
    /// it was not acknowledged; the caller keeps the event and retries.
    /// </summary>
    Task ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>
/// The shared Kafka producer for CHO events. Every service that builds its
/// own <c>ProducerBuilder</c> should move here. The configuration is the one
/// a transactional outbox needs:
/// <list type="bullet">
///   <item><c>enable.idempotence=true</c>: a retry inside the client never
///     writes a message twice or out of order on its partition;</item>
///   <item><c>acks=all</c>: acknowledged only once every in-sync replica has it,
///     so a broker failover after the acknowledgement loses nothing;</item>
///   <item>a bounded delivery timeout, so a broker outage fails the produce
///     (and the outbox keeps the event) instead of hanging the caller.</item>
/// </list>
/// Callers deduplicate on the event's own id: delivery is at least once.
/// </summary>
public sealed class ChoKafkaProducer : IChoEventProducer, IDisposable
{
    private readonly Lazy<IProducer<string, string>>? _producer;
    private readonly TimeSpan _flushTimeout;

    public ChoKafkaProducer(ChoKafkaProducerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IsConfigured = settings.IsConfigured;
        _flushTimeout = settings.DeliveryTimeout;
        if (IsConfigured)
            _producer = new Lazy<IProducer<string, string>>(() => new ProducerBuilder<string, string>(BuildConfig(settings)).Build());
    }

    public bool IsConfigured { get; }

    /// <summary>The producer configuration (exposed so it can be asserted on).</summary>
    public static ProducerConfig BuildConfig(ChoKafkaProducerSettings settings)
    {
        var timeoutMs = (int)settings.DeliveryTimeout.TotalMilliseconds;
        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            EnableIdempotence = true,
            Acks = Acks.All,
            MaxInFlight = 5,
            MessageTimeoutMs = timeoutMs,
            RequestTimeoutMs = timeoutMs,
            SocketTimeoutMs = timeoutMs,
        };
        if (!string.IsNullOrWhiteSpace(settings.SaslUsername))
        {
            config.SaslUsername = settings.SaslUsername;
            config.SaslPassword = settings.SaslPassword;
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
        }
        return config;
    }

    public async Task ProduceAsync(
        string topic, string key, string value, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (_producer is null)
            throw new InvalidOperationException("Kafka is not configured (Kafka:BootstrapServers); the event cannot be published.");

        var message = new Message<string, string> { Key = key, Value = value, Headers = new Headers() };
        foreach (var (name, header) in headers)
            message.Headers.Add(name, Encoding.UTF8.GetBytes(header));

        var result = await _producer.Value.ProduceAsync(topic, message, ct);
        if (result.Status != PersistenceStatus.Persisted)
            throw new ProduceException<string, string>(
                new Error(ErrorCode.Local_MsgTimedOut, $"Not acknowledged by the broker (status {result.Status})."), result);
    }

    public void Dispose()
    {
        if (_producer is { IsValueCreated: true })
        {
            try { _producer.Value.Flush(_flushTimeout); }
            catch (KafkaException) { /* the outbox still holds anything not acknowledged */ }
            _producer.Value.Dispose();
        }
    }
}
