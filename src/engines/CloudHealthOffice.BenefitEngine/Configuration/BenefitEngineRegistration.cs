using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.BenefitEngine.Configuration;

/// <summary>
/// DI registration for the Benefit Calculation Engine.
///
/// Usage in the host service's Program.cs:
///
///   // CHO-native mode (uses CHO's own benefit/accumulator stores)
///   builder.Services.AddBenefitEngine()
///       .UseChoBenefitPlanProvider()
///       .UseChoAccumulatorService();
///
///   // QNXT adapter mode (reads from QNXT)
///   builder.Services.AddBenefitEngine()
///       .UseQnxtBenefitPlanProvider(qnxtOptions)
///       .UseQnxtAccumulatorService(qnxtOptions);
///
///   // Hybrid mode (QNXT for benefits, CHO for accumulators)
///   builder.Services.AddBenefitEngine()
///       .UseQnxtBenefitPlanProvider(qnxtOptions)
///       .UseChoAccumulatorService();
/// </summary>
public static class BenefitEngineServiceCollectionExtensions
{
    /// <summary>
    /// Register the core benefit calculation engine and its dependencies.
    /// After calling this, chain .UseXxx methods to configure providers.
    /// </summary>
    public static BenefitEngineBuilder AddBenefitEngine(this IServiceCollection services)
    {
        // Core engine (always registered)
        services.AddScoped<IBenefitCalculationEngine, BenefitCalculationEngine>();
        services.AddScoped<IServiceCategoryResolver, ServiceCategoryResolver>();
        services.AddScoped<IBenefitRuleGate, BenefitRuleGate>();

        return new BenefitEngineBuilder(services);
    }
}

public class BenefitEngineBuilder
{
    private readonly IServiceCollection _services;

    public BenefitEngineBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Use CHO's own MongoDB/Cosmos-backed benefit plan configuration.
    /// Reads from the benefit-plan-service's data store.
    /// </summary>
    public BenefitEngineBuilder UseChoBenefitPlanProvider()
    {
        // Register the CHO-native implementation
        // Implementation class: ChoBenefitPlanProvider (in benefit-plan-service)
        _services.AddScoped<IBenefitPlanProvider, ChoBenefitPlanProvider>();
        return this;
    }

    /// <summary>
    /// Use CHO's own accumulator tracking store.
    ///
    /// Automatically selects the MongoDB or Cosmos DB repository based on
    /// whether <c>MongoDb:ConnectionString</c> is present in configuration.
    /// The host service must register the appropriate DB client before calling this:
    /// <list type="bullet">
    ///   <item>MongoDB mode: register <c>IMongoDatabase</c></item>
    ///   <item>Cosmos mode: register <c>CosmosClient</c></item>
    /// </list>
    /// The host service must also register an <c>IBenefitEngineTenantContext</c>
    /// implementation that provides the current tenant ID.
    /// </summary>
    public BenefitEngineBuilder UseChoAccumulatorService(IConfiguration? configuration = null)
    {
        if (configuration is not null &&
            !string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"]))
        {
            _services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();
        }
        else
        {
            _services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryCosmos>();
        }

        _services.AddScoped<IAccumulatorService, ChoAccumulatorService>();
        return this;
    }

// ── Redis-Backed Accumulator (runtime calculation) ──

    /// <summary>
    /// Use Redis-backed accumulators with runtime calculation from claim history.
    ///
    /// This follows the other platform patterns: accumulators are derived values calculated
    /// from finalized claim lines, not stored as mutable state. Redis serves as
    /// a hot cache with atomic increments; cache misses rebuild from claims.
    ///
    /// Prerequisites:
    /// <list type="bullet">
    ///   <item>Register <c>IConnectionMultiplexer</c> (StackExchange.Redis)</item>
    ///   <item>Register <c>IClaimsAccumulatorSource</c> (implemented by host service)</item>
    ///   <item>Register <c>IBenefitEngineTenantContext</c></item>
    ///   <item>Optionally register <c>IAccumulatorAuditWriter</c> for durable audit trail</item>
    /// </list>
    ///
    /// Usage:
    /// <code>
    ///   builder.Services.AddBenefitEngine()
    ///       .UseChoBenefitPlanProvider()
    ///       .UseRedisAccumulatorService();
    /// </code>
    /// </summary>
    public BenefitEngineBuilder UseRedisAccumulatorService()
    {
        _services.AddScoped<IAccumulatorService, RedisAccumulatorService>();
        return this;
    }    

    // ── QNXT Adapter Mode ──

    /// <summary>
    /// Use QNXT as the benefit plan configuration source.
    /// Reads from QNXT Plan/BenefitPlanDetail tables via API or direct DB.
    /// </summary>
    public BenefitEngineBuilder UseQnxtBenefitPlanProvider(Action<QnxtAdapterOptions>? configure = null)
    {
        var options = new QnxtAdapterOptions();
        configure?.Invoke(options);
        _services.AddSingleton(options);
        _services.AddScoped<IBenefitPlanProvider, QnxtBenefitPlanProvider>();
        return this;
    }

    /// <summary>
    /// Use QNXT as the accumulator source.
    /// Reads from QNXT AccumBalance tables.
    /// </summary>
    public BenefitEngineBuilder UseQnxtAccumulatorService(Action<QnxtAdapterOptions>? configure = null)
    {
        var options = new QnxtAdapterOptions();
        configure?.Invoke(options);
        _services.AddSingleton(options);
        _services.AddScoped<IAccumulatorService, QnxtAccumulatorService>();
        return this;
    }
}

/// <summary>
/// Configuration options for the QNXT adapter implementations.
/// </summary>
public class QnxtAdapterOptions
{
    /// <summary>
    /// QNXT API base URL (if using API integration).
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// QNXT database connection string (if using direct DB integration).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Integration mode.
    /// </summary>
    public QnxtIntegrationMode Mode { get; set; } = QnxtIntegrationMode.Api;

    /// <summary>
    /// Whether to shadow-write to CHO's own stores for portal/analytics.
    /// Only applicable when QNXT is the primary source.
    /// </summary>
    public bool ShadowWriteToCho { get; set; } = false;

    /// <summary>
    /// Cache duration for plan configuration (plans don't change often).
    /// </summary>
    public TimeSpan PlanCacheDuration { get; set; } = TimeSpan.FromMinutes(30);
}

public enum QnxtIntegrationMode
{
    /// <summary>
    /// Use QNXT web services / REST APIs.
    /// </summary>
    Api,

    /// <summary>
    /// Direct database queries (requires network access to QNXT DB).
    /// </summary>
    DirectDb,

    /// <summary>
    /// Read from a CHO-managed cache/extract of QNXT data.
    /// The extract is populated by a separate sync job.
    /// </summary>
    CachedExtract
}

// ═══════════════════════════════════════════════════════════════════
// PLACEHOLDER IMPLEMENTATIONS
//
// These are stubs that need real implementation.
// They're registered by the DI builder methods above.
// Each one should be in its own file in the real codebase.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// CHO-native benefit plan provider — reads from CHO's own data store.
/// TODO: Implement with actual MongoDB/Cosmos queries against benefit-plan-service's collections.
/// </summary>
internal class ChoBenefitPlanProvider : IBenefitPlanProvider
{
    public Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "ChoBenefitPlanProvider: Implement MongoDB/Cosmos queries against " +
            "benefit-plan-service's BenefitPlan + BenefitCategory + CostShareRule collections.");
    }
}

/// <summary>
/// QNXT adapter benefit plan provider.
/// TODO: Implement with QNXT API calls or direct DB queries.
/// Maps QNXT Plan/BenefitPlanDetail structures to BenefitPlanConfig.
/// </summary>
internal class QnxtBenefitPlanProvider : IBenefitPlanProvider
{
    private readonly QnxtAdapterOptions _options;

    public QnxtBenefitPlanProvider(QnxtAdapterOptions options)
    {
        _options = options;
    }

    public Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "QnxtBenefitPlanProvider: Implement QNXT Plan/BenefitPlanDetail → BenefitPlanConfig mapping. " +
            $"Mode: {_options.Mode}, ShadowWrite: {_options.ShadowWriteToCho}");
    }
}

/// <summary>
/// QNXT adapter accumulator service.
/// TODO: Implement with QNXT AccumBalance table queries.
/// </summary>
internal class QnxtAccumulatorService : IAccumulatorService
{
    private readonly QnxtAdapterOptions _options;

    public QnxtAccumulatorService(QnxtAdapterOptions options)
    {
        _options = options;
    }

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId,
        string planYear, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "QnxtAccumulatorService: Implement AccumBalance reads from QNXT.");
    }

    public Task ApplyUpdatesAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "QnxtAccumulatorService: Implement AccumBalance writes to QNXT " +
            "(or shadow-write to CHO if configured).");
    }

    public Task ReverseAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct = default)
    {
        throw new NotImplementedException("QnxtAccumulatorService: Implement accumulator reversal.");
    }

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default)
    {
        throw new NotImplementedException("QnxtAccumulatorService: Implement annual reset.");
    }
}
