using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Events.Kafka;
using CloudHealthOffice.PlanConfiguration;

namespace TenantService.Configuration;

/// <summary>Tells services a setting changed, so they drop cached answers.</summary>
public interface IConfigurationChangePublisher
{
    Task PublishAsync(ConfigurationChangedEvent change, CancellationToken ct = default);
}

/// <summary>
/// Publishes <c>configuration.changed.v1</c>, keyed by tenant and setting.
/// Best-effort by design: the change is already stored, and subscribers'
/// caches expire within <c>PlanSettings:CacheSeconds</c> if an event is lost
/// (plan-configuration.md §9). Without Kafka it publishes nothing.
/// </summary>
public sealed class KafkaConfigurationChangePublisher(IChoEventProducer producer) : IConfigurationChangePublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task PublishAsync(ConfigurationChangedEvent change, CancellationToken ct = default)
    {
        if (!producer.IsConfigured) return Task.CompletedTask;
        var headers = new Dictionary<string, string>
        {
            ["tenant-id"] = change.TenantId,
            ["event-type"] = "ConfigurationChanged",
            ["event-version"] = "1.0",
            ["event-id"] = change.EventId,
        };
        return producer.ProduceAsync(ConfigurationChangedEvent.Topic, $"{change.TenantId}|{change.Key}",
            JsonSerializer.Serialize(change, Json), headers, ct);
    }
}

/// <summary>Creates the configuration indexes at startup.</summary>
public sealed class ConfigurationIndexInitializer(MongoDB.Driver.IMongoDatabase database, ILogger<ConfigurationIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await new MongoConfigurationStore(database).EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create the configuration indexes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
