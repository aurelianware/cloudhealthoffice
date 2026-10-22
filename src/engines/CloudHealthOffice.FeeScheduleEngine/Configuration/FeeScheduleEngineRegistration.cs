using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

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

    /// <summary>
    /// Turns on the dual-read window of ADR 016 phase 4: every claim is priced
    /// with both the legacy and the canonical provider contract, <b>paid on
    /// legacy</b>, and the difference is reported per line as
    /// <c>cho.pricing.contract_parity.total</c>.
    ///
    /// <para>
    /// Chain it after <see cref="UseMongoRepositories"/>. It is a no-op unless
    /// <c>FeeScheduleEngine:ContractDualRead:Enabled</c> is true, so a service
    /// can call it unconditionally and a deployment decides when a tenant enters
    /// the window — see <see cref="ContractDualReadOptions"/>.
    /// </para>
    ///
    /// <para>
    /// Inside the window a claim costs two pricing passes. The shadow pass reads
    /// no database of its own: it prices out of the contracts the paid pass
    /// already fetched, so the two passes cannot see different stores, and a
    /// shadow failure can never stop a claim from being paid.
    /// </para>
    /// </summary>
    public FeeScheduleEngineBuilder UseDualReadContracts(IConfiguration configuration)
    {
        var options = ContractDualReadOptions.FromConfiguration(configuration);
        _services.AddSingleton(options);

        if (!options.Enabled)
            return this;

        _services.AddScoped<ContractShadowLedger>();
        _services.AddScoped<IPricingParityRecorder, MetricPricingParityRecorder>();

        var scheduleCollection = configuration["FeeScheduleEngine:FeeScheduleCollection"] ?? "FeeSchedules";

        _services.AddScoped<IProviderContractRepository>(sp => new DualReadProviderContractRepository(
            // The legacy read keeps the cache it already had: inside the window
            // it is still the read that pays the claim.
            legacy: CreateFeeScheduleCache<FeeScheduleRepositoryMongo>(sp),
            canonical: new FeeScheduleRepositoryMongo(
                sp.GetRequiredService<IMongoDatabase>(),
                scheduleCollection,
                options.CanonicalContractCollection,
                sp.GetRequiredService<ILogger<FeeScheduleRepositoryMongo>>()),
            ledger: sp.GetRequiredService<ContractShadowLedger>(),
            options: options,
            logger: sp.GetRequiredService<ILogger<DualReadProviderContractRepository>>()));

        _services.AddScoped<IRateResolutionService>(sp =>
        {
            var schedules = sp.GetRequiredService<IFeeScheduleRepository>();
            var engineLogger = sp.GetRequiredService<ILogger<RateResolutionService>>();
            var ledger = sp.GetRequiredService<ContractShadowLedger>();

            return new ShadowPricingRateResolutionService(
                paid: new RateResolutionService(
                    schedules, sp.GetRequiredService<IProviderContractRepository>(), engineLogger),
                shadow: new RateResolutionService(
                    schedules, new ShadowContractRepository(ledger), engineLogger),
                ledger: ledger,
                recorder: sp.GetRequiredService<IPricingParityRecorder>(),
                logger: sp.GetRequiredService<ILogger<ShadowPricingRateResolutionService>>());
        });

        return this;
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
