using CloudHealthOffice.BenefitEngine.Services;
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
    /// </summary>
    public BenefitEngineBuilder UseChoAccumulatorService()
    {
        _services.AddScoped<IAccumulatorService, ChoAccumulatorService>();
        return this;
    }

    /// <summary>
    /// Use CHO's MongoDB-backed service category mappings.
    /// </summary>
    public BenefitEngineBuilder UseChoServiceCategoryMappings()
    {
        _services.AddScoped<IServiceCategoryMappingRepository, ChoServiceCategoryMappingRepository>();
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
/// CHO-native accumulator service — reads/writes CHO's own accumulator store.
/// TODO: Implement with actual MongoDB/Cosmos queries + optimistic concurrency.
/// </summary>
internal class ChoAccumulatorService : IAccumulatorService
{
    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId,
        string planYear, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "ChoAccumulatorService: Implement accumulator reads. " +
            "Collection key: (memberId, benefitPlanId, planYear). " +
            "Use optimistic concurrency (version field) for updates.");
    }

    public Task ApplyUpdatesAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        throw new NotImplementedException("ChoAccumulatorService: Implement accumulator writes with optimistic concurrency.");
    }

    public Task ReverseAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct = default)
    {
        throw new NotImplementedException("ChoAccumulatorService: Implement accumulator reversal.");
    }

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default)
    {
        throw new NotImplementedException("ChoAccumulatorService: Implement annual accumulator reset.");
    }
}

/// <summary>
/// CHO-native service category mapping repository.
/// TODO: Implement with actual MongoDB/Cosmos queries.
/// </summary>
internal class ChoServiceCategoryMappingRepository : IServiceCategoryMappingRepository
{
    public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "ChoServiceCategoryMappingRepository: Implement MongoDB/Cosmos queries " +
            "against ServiceCategoryMapping collection.");
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
