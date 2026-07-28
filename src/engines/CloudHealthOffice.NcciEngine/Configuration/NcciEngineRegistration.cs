using CloudHealthOffice.NcciEngine.Data;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Configuration;

/// <summary>
/// DI registration for the NCCI Edit Engine.
///
/// Usage in the host service's Program.cs:
///
///   // CHO-native mode (Cosmos DB)
///   builder.Services.AddNcciEngine()
///       .UseCosmosRepository();
///
///   // CHO-native mode (MongoDB)
///   builder.Services.AddNcciEngine()
///       .UseMongoRepository();
///
///   // Auto-detect from configuration
///   builder.Services.AddNcciEngine()
///       .UseRepositoryFromConfiguration(builder.Configuration);
///
/// Seed data (development/new tenant bootstrap):
///
///   // In Program.cs after Build():
///   await app.SeedNcciDataAsync(tenantId: "your-tenant");
/// </summary>
public static class NcciEngineServiceCollectionExtensions
{
    /// <summary>
    /// Register the core NCCI/MUE edit service.
    /// Chain a .UseXxx method to configure the persistence layer.
    /// </summary>
    public static NcciEngineBuilder AddNcciEngine(this IServiceCollection services)
    {
        services.AddSingleton<NcciLookupCache>();
        services.AddScoped<INcciEditService, NcciEditService>();
        return new NcciEngineBuilder(services);
    }

    /// <summary>
    /// Seed NCCI baseline data for a tenant from the built-in Q1 2025 seed set.
    /// Call this once during first-run setup or in development environments.
    /// Safe to call multiple times — uses upsert semantics.
    /// </summary>
    public static async Task SeedNcciDataAsync(
        this IServiceProvider services,
        string tenantId,
        string quarter = "2025Q1")
    {
        using var scope = services.CreateScope();
        var ncciService = scope.ServiceProvider.GetRequiredService<INcciEditService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<NcciEngineBuilder>>();

        var pairs = NcciSeedData.BuildNcciPairs(tenantId);
        var mues  = NcciSeedData.BuildMueEntries(tenantId);

        var (pairsWritten, mueWritten) = await ncciService.ImportQuarterlyUpdateAsync(
            tenantId, quarter, pairs, mues);

        // Record the version
        var repo = scope.ServiceProvider.GetRequiredService<INcciRepository>();
        await repo.SaveVersionAsync(new NcciTableVersion
        {
            TenantId       = tenantId,
            Quarter        = quarter,
            ImportedAt     = DateTime.UtcNow,
            NcciPairCount  = pairsWritten,
            MueEntryCount  = mueWritten,
            EffectiveDate  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        logger.LogInformation(
            "NCCI seed complete for tenant {TenantId} ({Quarter}): " +
            "{Pairs} pairs, {Mue} MUE entries",
            tenantId, quarter, pairsWritten, mueWritten);
    }
}

/// <summary>
/// Fluent builder for configuring the NCCI Engine persistence layer.
/// </summary>
public class NcciEngineBuilder
{
    private readonly IServiceCollection _services;

    public NcciEngineBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Use Cosmos DB for NCCI/MUE storage.
    /// Requires <c>CosmosClient</c> to be registered by the host.
    /// </summary>
    public NcciEngineBuilder UseCosmosRepository()
    {
        _services.AddScoped<INcciRepository, NcciRepositoryCosmos>();
        return this;
    }

    /// <summary>
    /// Use MongoDB for NCCI/MUE storage.
    /// Requires <c>IMongoDatabase</c> to be registered by the host.
    /// </summary>
    public NcciEngineBuilder UseMongoRepository()
    {
        _services.AddScoped<INcciRepository, NcciRepositoryMongo>();
        _services.AddSingleton<IHostedService>(sp =>
            new NcciMongoIndexInitializer(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<NcciMongoIndexInitializer>>()));
        return this;
    }

    /// <summary>
    /// Auto-detect backend from configuration:
    ///   <c>MongoDb:ConnectionString</c> present → MongoDB
    ///   Otherwise → Cosmos DB
    /// </summary>
    public NcciEngineBuilder UseRepositoryFromConfiguration(IConfiguration configuration)
    {
        return !string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"])
            ? UseMongoRepository()
            : UseCosmosRepository();
    }
}
