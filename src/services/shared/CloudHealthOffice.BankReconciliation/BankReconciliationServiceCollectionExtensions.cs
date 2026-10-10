using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.BankReconciliation;

public static class BankReconciliationServiceCollectionExtensions
{
    /// <summary>
    /// Registers BAI2 ingestion, the reconciliation runner and store, and the
    /// default (logging) event sink. MongoDB is required: without it the store
    /// refuses every call (<see cref="UnsupportedBankReconciliationStore"/>).
    /// Field protection is registered for <paramref name="serviceName"/> if the
    /// service has not registered it already. The service registers its own
    /// <see cref="IBankTransactionMatcher"/>.
    /// </summary>
    public static IServiceCollection AddChoBankReconciliation(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        ChoDatabaseProvider? databaseProvider)
    {
        services.Configure<BankReconciliationOptions>(o =>
        {
            configuration.GetSection(BankReconciliationOptions.SectionName).Bind(o);
            o.ServiceName = serviceName;
        });

        if (!services.Any(d => d.ServiceType == typeof(IFieldProtector)))
            services.AddChoFieldProtection(configuration, environment, serviceName);

        if (databaseProvider == ChoDatabaseProvider.MongoDb)
        {
            services.TryAddScoped<IBankReconciliationStore>(sp =>
                sp.GetService<IMongoDatabase>() is { } db
                    ? new MongoBankReconciliationStore(db)
                    : new UnsupportedBankReconciliationStore());
            services.AddHostedService<BankReconciliationIndexInitializer>();
        }
        else
        {
            services.TryAddSingleton<IBankReconciliationStore, UnsupportedBankReconciliationStore>();
        }

        services.TryAddSingleton<IBankReconciliationEventSink, LoggingBankReconciliationEventSink>();
        services.TryAddScoped<BankStatementIngestion>();
        services.TryAddScoped<BankReconciliationRunner>();
        services.TryAddScoped<BankStatementSupersessionService>();
        return services;
    }
}

/// <summary>Creates the bank reconciliation indexes (including the unique bank file key) at startup.</summary>
public sealed class BankReconciliationIndexInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BankReconciliationIndexInitializer> _logger;

    public BankReconciliationIndexInitializer(IServiceProvider services, ILogger<BankReconciliationIndexInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService<IBankReconciliationStore>() is MongoBankReconciliationStore store)
                await store.EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The unique bank-file-key index backs the cross-file duplicate check; the
            // ingestion also checks it before inserting, so a missing index narrows to a race.
            _logger.LogError(ex, "Could not create bank reconciliation indexes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
