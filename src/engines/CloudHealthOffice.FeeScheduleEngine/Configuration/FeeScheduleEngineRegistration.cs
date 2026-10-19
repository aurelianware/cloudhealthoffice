using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FeeScheduleEngine.Configuration;

/// <summary>
/// DI registration for the Fee Schedule Engine.
///
/// Usage in the host service's Program.cs (MongoDB is the only store):
///
///   builder.Services.AddFeeScheduleEngine()
///       .UseMongoRepositories();
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
    /// Kept for existing callers: binds the MongoDB repositories, the only
    /// store. <paramref name="configuration"/> no longer selects a backend.
    /// </summary>
    public FeeScheduleEngineBuilder UseRepositoriesFromConfiguration(IConfiguration configuration)
        => UseMongoRepositories();

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
