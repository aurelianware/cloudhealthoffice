using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Registers the vendor-neutral healthcare transaction gateway abstraction.
///
/// Registration follows the existing Cloud Health Office pattern (see
/// <c>AddChoMessaging</c>): options are bound from configuration, one or more
/// <see cref="IHealthcareTransactionGateway"/> implementations are registered,
/// and an <see cref="IHealthcareGatewayResolver"/> selects among them. Callers
/// depend on the resolver / capability interfaces via constructor injection —
/// there is no service-locator access to concrete gateways.
/// </summary>
public static class HealthcareGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="HealthcareTransactionOptions"/> from the
    /// <c>HealthcareTransactions</c> section, register the resolver, and add the
    /// mock development gateway. Future vendor gateways (Stedi, Availity, direct
    /// X12) are added with <see cref="AddHealthcareGateway{TGateway}"/> without
    /// changing this method.
    /// </summary>
    public static IServiceCollection AddChoHealthcareGateways(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<HealthcareTransactionOptions>()
            .Bind(configuration.GetSection(HealthcareTransactionOptions.SectionName));

        services.TryAddSingleton<IHealthcareGatewayResolver, HealthcareGatewayResolver>();
        RegisterClaimLifecycleStores(services, configuration);
        services.TryAddSingleton<IClaimAttachmentContentStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HealthcareTransactionOptions>>().Value.ClaimAttachments;
            return new InMemoryClaimAttachmentContentStore(opts);
        });
        services.TryAddSingleton<IClaimAcknowledgmentProcessor>(sp =>
            new ClaimAcknowledgmentProcessor(
                sp.GetRequiredService<IClaimAcknowledgmentStore>(),
                sp.GetRequiredService<IClaimTransmissionStore>(),
                sp.GetRequiredService<ILogger<ClaimAcknowledgmentProcessor>>(),
                sp.GetService<IMessageBus>(),
                sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IClaimAcknowledgmentIngress, ClaimAcknowledgmentIngress>();
        services.AddHostedService<ClaimLifecycleIndexHostedService>();
        services.AddHostedService<ClaimLifecycleStoreGuard>();
        services.AddHostedService<ClaimAcknowledgmentOutboxPublisher>();

        // Canonical payer identity is shared by every gateway implementation.
        services.AddChoPayerReference(configuration);

        // The mock gateway is always available so the abstraction resolves in
        // development and test even when no vendor is configured. It is the
        // default target of HealthcareTransactions:DefaultGateway.
        services.AddHealthcareGateway<MockHealthcareGateway>();

        // Register the Stedi gateway whenever it is configured or explicitly
        // selected as the default. When Stedi is selected but its configuration
        // is incomplete, the Stedi gateway (not Mock) is resolved and returns a
        // clear Configuration error — there is no silent fallback to Mock.
        var defaultGateway = configuration[$"{HealthcareTransactionOptions.SectionName}:DefaultGateway"];
        var stediConfigured = configuration.GetSection(StediGatewayOptions.SectionPath).Exists();
        var stediIsDefault = string.Equals(defaultGateway, StediHealthcareGateway.GatewayName, StringComparison.OrdinalIgnoreCase);
        if (stediConfigured || stediIsDefault)
        {
            services.AddStediHealthcareGateway(configuration);
        }

        return services;
    }

    /// <summary>
    /// Register an additional <see cref="IHealthcareTransactionGateway"/>
    /// implementation as a singleton. Idempotent per implementation type.
    /// </summary>
    public static IServiceCollection AddHealthcareGateway<TGateway>(this IServiceCollection services)
        where TGateway : class, IHealthcareTransactionGateway
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthcareTransactionGateway, TGateway>());
        return services;
    }

    private static void RegisterClaimLifecycleStores(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(sp => CreateLifecycleBox(sp, configuration));
        services.TryAddSingleton<IClaimTransmissionStore>(sp =>
            sp.GetRequiredService<ClaimLifecycleStoreBox>().Transmissions);
        services.TryAddSingleton<IClaimAcknowledgmentStore>(sp =>
            sp.GetRequiredService<ClaimLifecycleStoreBox>().Acknowledgments);
        services.TryAddSingleton<IClaimAcknowledgmentCursorStore>(sp =>
            sp.GetRequiredService<ClaimLifecycleStoreBox>().Cursors);
        services.TryAddSingleton<IClaimAttachmentTransmissionStore>(sp =>
            sp.GetRequiredService<ClaimLifecycleStoreBox>().Attachments);
        services.TryAddSingleton<CloudHealthOffice.Infrastructure.Responders.IInboundClaimAttachmentReceiptStore>(sp =>
            sp.GetRequiredService<ClaimLifecycleStoreBox>().InboundAttachments);
    }

    private static ClaimLifecycleStoreBox CreateLifecycleBox(IServiceProvider sp, IConfiguration configuration)
    {
        var options = sp.GetRequiredService<IOptions<HealthcareTransactionOptions>>().Value.ClaimLifecycle;
        var env = sp.GetService<IHostEnvironment>();
        var mongoClient = sp.GetService<IMongoClient>();
        var requireMongo = options.UseMongo ||
            (string.IsNullOrWhiteSpace(options.Store) &&
             env is not null &&
             !env.IsDevelopment() &&
             !options.AllowInMemoryInNonDevelopment);

        if (requireMongo)
        {
            if (mongoClient is null)
            {
                throw new InvalidOperationException(
                    "HealthcareTransactions:ClaimLifecycle:Store is Mongo but IMongoClient is not registered.");
            }

            var databaseName = string.IsNullOrWhiteSpace(options.MongoDatabaseName)
                ? configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice"
                : options.MongoDatabaseName;
            var mongo = new MongoClaimLifecycleStore(mongoClient.GetDatabase(databaseName), options);
            return new ClaimLifecycleStoreBox(mongo, mongo, mongo, mongo, mongo);
        }

        return new ClaimLifecycleStoreBox(
            new InMemoryClaimTransmissionStore(),
            new InMemoryClaimAcknowledgmentStore(),
            new InMemoryClaimAcknowledgmentCursorStore(),
            new InMemoryClaimAttachmentTransmissionStore(),
            new CloudHealthOffice.Infrastructure.Responders.InMemoryInboundClaimAttachmentReceiptStore());
    }

    internal sealed class ClaimLifecycleStoreBox
    {
        public ClaimLifecycleStoreBox(
            IClaimTransmissionStore transmissions,
            IClaimAcknowledgmentStore acknowledgments,
            IClaimAcknowledgmentCursorStore cursors,
            IClaimAttachmentTransmissionStore attachments,
            CloudHealthOffice.Infrastructure.Responders.IInboundClaimAttachmentReceiptStore inboundAttachments)
        {
            Transmissions = transmissions;
            Acknowledgments = acknowledgments;
            Cursors = cursors;
            Attachments = attachments;
            InboundAttachments = inboundAttachments;
        }

        public IClaimTransmissionStore Transmissions { get; }
        public IClaimAcknowledgmentStore Acknowledgments { get; }
        public IClaimAcknowledgmentCursorStore Cursors { get; }
        public IClaimAttachmentTransmissionStore Attachments { get; }
        public CloudHealthOffice.Infrastructure.Responders.IInboundClaimAttachmentReceiptStore InboundAttachments { get; }
    }
}
