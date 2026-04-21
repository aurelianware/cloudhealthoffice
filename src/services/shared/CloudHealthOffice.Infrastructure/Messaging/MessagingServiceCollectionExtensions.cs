using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// Registers <see cref="IMessageBus"/> and resolves the backend from
/// configuration + environment.
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IMessageBus"/> as a singleton.
    ///
    /// Backend resolution:
    ///   <c>Auto</c>       — prod + connection string present → ServiceBus;
    ///                        otherwise InMemory (with a warning if prod).
    ///   <c>ServiceBus</c> — forced; throws if connection string missing.
    ///   <c>InMemory</c>   — forced.
    ///   <c>Null</c>       — forced no-op.
    ///
    /// Config keys: <c>Messaging:Backend</c>, <c>Messaging:ServiceBusConnectionString</c>.
    ///
    /// Back-compat: <c>BatchEligibility:ServiceBus:ConnectionString</c> and
    /// <c>IdCard:QnxtMirror:ServiceBusConnectionString</c> are honoured as
    /// fallbacks and emit a single deprecation warning at startup. Follow-up:
    /// remove these fallbacks after one release cycle.
    /// </summary>
    public static IServiceCollection AddChoMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new MessagingOptions();
        configuration.GetSection(MessagingOptions.SectionName).Bind(options);

        var (connectionString, deprecatedKey) = ResolveConnectionString(configuration, options);
        options.ServiceBusConnectionString = connectionString;
        services.AddSingleton(options);

        var decision = ResolveBackend(options, environment);

        services.AddSingleton<IMessageBus>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("CloudHealthOffice.Infrastructure.Messaging");

            logger.LogInformation(
                "IMessageBus={Backend} ({Reason})",
                decision.Backend, decision.Reason);

            // Auto-resolving to InMemory outside Development is almost always
            // a misconfiguration: messages go into a process-local channel
            // and are lost on restart. Emit at Warning so it shows up in any
            // routine log triage.
            if (decision.Backend == MessagingBackend.InMemory &&
                !environment.IsDevelopment() &&
                string.Equals((options.Backend ?? "Auto").Trim(), "Auto",
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "IMessageBus resolved to InMemory in {Environment}. " +
                    "Configure Messaging:ServiceBusConnectionString for durable delivery.",
                    environment.EnvironmentName);
            }

            if (deprecatedKey is not null)
            {
                logger.LogWarning(
                    "Config key '{Deprecated}' is deprecated; migrate to '{Canonical}'. " +
                    "Falling back for this release.",
                    deprecatedKey, "Messaging:ServiceBusConnectionString");
            }

            return decision.Backend switch
            {
                MessagingBackend.ServiceBus => new ServiceBusMessageBus(
                    new ServiceBusClient(options.ServiceBusConnectionString!),
                    loggerFactory.CreateLogger<ServiceBusMessageBus>()),
                MessagingBackend.Null => new NullMessageBus(),
                _ => new InMemoryMessageBus(
                    options,
                    loggerFactory.CreateLogger<InMemoryMessageBus>())
            };
        });

        return services;
    }

    internal static (string? ConnectionString, string? DeprecatedKey) ResolveConnectionString(
        IConfiguration configuration, MessagingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceBusConnectionString))
            return (options.ServiceBusConnectionString, null);

        // Legacy fallbacks. One warning per startup; remove after one release.
        var batchEligibility = configuration["BatchEligibility:ServiceBus:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(batchEligibility))
            return (batchEligibility, "BatchEligibility:ServiceBus:ConnectionString");

        var idcardMirror = configuration["IdCard:QnxtMirror:ServiceBusConnectionString"];
        if (!string.IsNullOrWhiteSpace(idcardMirror))
            return (idcardMirror, "IdCard:QnxtMirror:ServiceBusConnectionString");

        return (null, null);
    }

    internal static BackendDecision ResolveBackend(
        MessagingOptions options, IHostEnvironment environment)
    {
        var backend = (options.Backend ?? "Auto").Trim();
        var hasCs = !string.IsNullOrWhiteSpace(options.ServiceBusConnectionString);

        return backend.ToLowerInvariant() switch
        {
            "servicebus" when hasCs => new BackendDecision(
                MessagingBackend.ServiceBus,
                $"forced; env={environment.EnvironmentName}"),
            "servicebus" => throw new InvalidOperationException(
                "Messaging:Backend=ServiceBus requires Messaging:ServiceBusConnectionString (or a legacy fallback key)."),
            "inmemory" => new BackendDecision(
                MessagingBackend.InMemory,
                $"forced; env={environment.EnvironmentName}"),
            "null" => new BackendDecision(
                MessagingBackend.Null,
                $"forced; env={environment.EnvironmentName}"),
            "auto" or "" when environment.IsDevelopment() => new BackendDecision(
                MessagingBackend.InMemory,
                $"Auto; env=Development"),
            "auto" or "" when hasCs => new BackendDecision(
                MessagingBackend.ServiceBus,
                $"Auto; ConnectionString configured; env={environment.EnvironmentName}"),
            "auto" or "" => new BackendDecision(
                MessagingBackend.InMemory,
                $"Auto; no ConnectionString; env={environment.EnvironmentName}"),
            _ => throw new InvalidOperationException(
                $"Messaging:Backend='{options.Backend}' is not recognised. Use Auto, ServiceBus, InMemory, or Null.")
        };
    }

    internal enum MessagingBackend { Auto, ServiceBus, InMemory, Null }
    internal record BackendDecision(MessagingBackend Backend, string Reason);
}
