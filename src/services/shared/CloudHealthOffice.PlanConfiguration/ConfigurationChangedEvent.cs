using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// Published by tenant-service after every applied change, so services drop
/// cached answers at once. Carries no values and no PHI: subscribers re-read.
/// </summary>
public sealed record ConfigurationChangedEvent
{
    public const string Topic = "configuration.changed.v1";

    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public required string TenantId { get; init; }
    public required string Key { get; init; }
    public required SettingLevel Level { get; init; }
    public string ScopeId { get; init; } = string.Empty;
    public int Version { get; init; }
    public DateTime ChangedAt { get; init; }
}

/// <summary>
/// Listens for <see cref="ConfigurationChangedEvent"/> and evicts cached
/// answers. Each process reads in its own consumer group from the latest
/// offset (it needs every change, not a share of them). Without Kafka it does
/// nothing and the cache expiry applies.
/// </summary>
public sealed class ConfigurationChangeListener(
    IPlanSettings settings,
    IConfiguration configuration,
    ILogger<ConfigurationChangeListener> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap))
        {
            logger.LogInformation("Kafka not configured; plan settings rely on the cache expiry");
            return Task.CompletedTask;
        }
        return Task.Run(() => Consume(bootstrap, stoppingToken), stoppingToken);
    }

    private void Consume(string bootstrap, CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = $"{configuration["Kafka:ClientId"] ?? "cho"}.plan-settings.{Environment.MachineName}.{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            SaslUsername = configuration["Kafka:SaslUsername"],
            SaslPassword = configuration["Kafka:SaslPassword"],
        };
        if (!string.IsNullOrEmpty(config.SaslUsername))
        {
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
            config.SaslMechanism = SaslMechanism.Plain;
        }

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(ConfigurationChangedEvent.Topic);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(ct);
                    if (result?.Message?.Value is not { } json) continue;
                    var change = JsonSerializer.Deserialize<ConfigurationChangedEvent>(json, HttpPlanSettings.Json);
                    if (change is not null) settings.Invalidate(change.TenantId, change.Key);
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Could not read a configuration change; cached settings expire on their own");
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Skipped an unreadable configuration change event");
                }
            }
            consumer.Close();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Configuration change listener stopped; cached settings expire on their own");
        }
    }
}

public static class PlanSettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPlanSettings"/> reading from tenant-service
    /// (<c>Services:TenantService</c>), cached for <c>PlanSettings:CacheSeconds</c>
    /// and invalidated by <c>configuration.changed.v1</c> when Kafka is configured.
    /// The outbound CHO token handler is added to the client by AddChoAuthentication.
    /// </summary>
    public static IServiceCollection AddPlanSettingsClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PlanSettingsOptions.SectionName).Get<PlanSettingsOptions>() ?? new PlanSettingsOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpClient(HttpPlanSettings.HttpClientName, client =>
        {
            client.BaseAddress = new Uri((configuration["Services:TenantService"] ?? "http://tenant-service").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).SetHandlerLifetime(TimeSpan.FromMinutes(5));
        services.TryAddSingleton<IPlanSettings, HttpPlanSettings>();
        services.AddHostedService<ConfigurationChangeListener>();
        return services;
    }
}
