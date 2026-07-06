using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FeeScheduleEngine.Configuration;

/// <summary>
/// DI registration for the Fee Schedule Engine.
///
/// Usage in the host service's Program.cs:
///
///   // CHO-native mode (Cosmos DB)
///   builder.Services.AddFeeScheduleEngine()
///       .UseCosmosRepositories();
///
///   // CHO-native mode (MongoDB)
///   builder.Services.AddFeeScheduleEngine()
///       .UseMongoRepositories();
///
///   // Auto-detect from configuration (MongoDb:ConnectionString present → Mongo, else Cosmos)
///   builder.Services.AddFeeScheduleEngine()
///       .UseRepositoriesFromConfiguration(builder.Configuration);
/// </summary>
public static class FeeScheduleEngineServiceCollectionExtensions
{
    /// <summary>
    /// Register the core rate resolution engine.
    /// Chain a .UseXxx method to configure the persistence layer.
    /// </summary>
    public static FeeScheduleEngineBuilder AddFeeScheduleEngine(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IRateResolutionService, RateResolutionService>();
        return new FeeScheduleEngineBuilder(services);
    }
}

public class FeeScheduleEngineBuilder
{
    private readonly IServiceCollection _services;

    public FeeScheduleEngineBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Use Cosmos DB for fee schedule and provider contract storage.
    /// Requires <c>CosmosClient</c> to be registered by the host.
    /// </summary>
    public FeeScheduleEngineBuilder UseCosmosRepositories()
    {
        _services.AddScoped<FeeScheduleRepositoryCosmos>();
        _services.AddScoped<IFeeScheduleRepository>(CreateFeeScheduleCache<FeeScheduleRepositoryCosmos>);
        _services.AddScoped<IProviderContractRepository>(CreateFeeScheduleCache<FeeScheduleRepositoryCosmos>);
        return this;
    }

    /// <summary>
    /// Use MongoDB for fee schedule and provider contract storage.
    /// Requires <c>IMongoDatabase</c> to be registered by the host.
    /// </summary>
    public FeeScheduleEngineBuilder UseMongoRepositories()
    {
        _services.AddScoped<FeeScheduleRepositoryMongo>();
        _services.AddScoped<IFeeScheduleRepository>(CreateFeeScheduleCache<FeeScheduleRepositoryMongo>);
        _services.AddScoped<IProviderContractRepository>(CreateFeeScheduleCache<FeeScheduleRepositoryMongo>);
        return this;
    }

    /// <summary>
    /// Auto-detect the database backend from configuration:
    ///   <c>MongoDb:ConnectionString</c> present → MongoDB
    ///   Otherwise → Cosmos DB
    /// </summary>
    public FeeScheduleEngineBuilder UseRepositoriesFromConfiguration(IConfiguration configuration)
    {
        return !string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"])
            ? UseMongoRepositories()
            : UseCosmosRepositories();
    }

    private static CachingFeeScheduleRepository CreateFeeScheduleCache<TRepository>(IServiceProvider sp)
        where TRepository : class, IFeeScheduleRepository, IProviderContractRepository
    {
        var inner = sp.GetRequiredService<TRepository>();
        return new CachingFeeScheduleRepository(
            inner,
            inner,
            sp.GetRequiredService<IMemoryCache>());
    }
}
