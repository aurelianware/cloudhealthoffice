using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using CloudHealthOffice.ProviderEnrollmentService.Lifecycle;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using CloudHealthOffice.ProviderEnrollmentService.Sources.California;
using CloudHealthOffice.ProviderEnrollmentService.Sources.CrossState;
using CloudHealthOffice.ProviderEnrollmentService.Sources.Florida;
using CloudHealthOffice.ProviderEnrollmentService.Sources.NewYork;
using CloudHealthOffice.ProviderEnrollmentService.Sources.Texas;
using CloudHealthOffice.ProviderEnrollmentService.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Configuration;

/// <summary>
/// DI registration for CloudHealthOffice.ProviderEnrollmentService.
///
/// ── Typical usage ─────────────────────────────────────────────────────────
///
/// // Texas Medicaid MCO tenant (Medicaid + Exchange) — v1:
/// builder.Services
///     .AddProviderEnrollmentService(builder.Configuration)
///     .UseCosmosRepositories()
///     .WithRedisTenantConfigCache()
///     .WithTexasSource()
///     .WithCaqhSource()
///     .WithNightlyBatchSync()
///     .WithRevalidationAlerts();
///
/// // Multi-state — v3+:
/// builder.Services
///     .AddProviderEnrollmentService(builder.Configuration)
///     .UseRepositoriesFromConfiguration(builder.Configuration)
///     .WithRedisTenantConfigCache()
///     .WithAllStateSources();
///
/// ── Tenant config cache ───────────────────────────────────────────────────
///
/// .WithRedisTenantConfigCache() wraps ITenantEnrollmentConfigRepository with
/// a Redis read-through decorator (RedisTenantEnrollmentConfigRepository).
///
///   Cache key:   enrollment:config:{tenantId}
///   Default TTL: 5 min (ProviderEnrollmentOptions.TenantConfigCacheTtl)
///   Invalidation: UpsertAsync and DeleteAsync immediately delete the Redis key
///   Redis failure: falls through to backing store — gate remains functional
///
/// Lower the TTL during rollout so a gate mode flip (Warn → Enforce)
/// propagates within one cache window across all pods.
///
/// ── Gate enforcement hierarchy ────────────────────────────────────────────
///
/// Per request: TenantEnrollmentConfig.ResolveFor(lob)
///   LOB override (non-null fields) → tenant default → Disabled (no config doc)
///
/// appsettings.json additions:
///   "ProviderEnrollmentService": {
///     "CacheTtl": "04:00:00",
///     "TenantConfigCacheTtl": "00:05:00",
///     "RevalidationWarningDays": 90,
///     "EnabledStateCodes": [],
///     "Tmhp": { "BaseUrl": "...", "ApiKey": "..." },
///     "Caqh": { "BaseUrl": "...", "Username": "...", "Password": "..." }
///   }
/// </summary>
public static class ProviderEnrollmentServiceCollectionExtensions
{
    public static ProviderEnrollmentServiceBuilder AddProviderEnrollmentService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ProviderEnrollmentOptions>(
            configuration.GetSection(ProviderEnrollmentOptions.SectionName));

        services.AddScoped<MultiStateEnrollmentAggregator>();
        services.AddScoped<IEnrollmentDecisionGate, StateEnrollmentGate>();
        // IHttpContextAccessor — needed by StateEnrollmentGate to resolve tenantId.
        // AddHttpContextAccessor is idempotent — safe even if the host already registered it.
        services.AddHttpContextAccessor();

        return new ProviderEnrollmentServiceBuilder(services);
    }
}

public sealed class ProviderEnrollmentServiceBuilder
{
    private readonly IServiceCollection _services;

    internal ProviderEnrollmentServiceBuilder(IServiceCollection services)
    {
        _services = services;
    }

    // ── Persistence ───────────────────────────────────────────────

    /// <summary>
    /// Use Cosmos DB for the enrollment record cache and tenant config store.
    /// Requires CosmosClient to be registered by the host.
    ///
    /// Cosmos containers required:
    ///   enrollment-cache         — partition key: /stateCode, TTL enabled
    ///   enrollment-tenant-config — partition key: /tenantId,  no TTL
    /// </summary>
    public ProviderEnrollmentServiceBuilder UseCosmosRepositories()
    {
        _services.AddScoped<IEnrollmentRepository, EnrollmentRepositoryCosmos>();
        _services.AddScoped<TenantEnrollmentConfigRepositoryCosmos>();

        // Default binding — overridden by WithRedisTenantConfigCache() if called
        _services.AddScoped<ITenantEnrollmentConfigRepository>(sp =>
            sp.GetRequiredService<TenantEnrollmentConfigRepositoryCosmos>());

        return this;
    }

    /// <summary>
    /// Use MongoDB for the enrollment record cache and tenant config store.
    /// Requires IMongoDatabase to be registered by the host.
    ///
    /// Collections created automatically:
    ///   enrollment_cache          — TTL index on cachedAt
    ///   enrollment_tenant_config  — unique index on tenantId
    /// </summary>
    public ProviderEnrollmentServiceBuilder UseMongoRepositories()
    {
        _services.AddScoped<IEnrollmentRepository, EnrollmentRepositoryMongo>();
        _services.AddScoped<TenantEnrollmentConfigRepositoryMongo>();

        _services.AddScoped<ITenantEnrollmentConfigRepository>(sp =>
            sp.GetRequiredService<TenantEnrollmentConfigRepositoryMongo>());

        return this;
    }

    /// <summary>
    /// Auto-detect the database backend from configuration:
    ///   MongoDb:ConnectionString present → MongoDB, else Cosmos DB.
    /// </summary>
    public ProviderEnrollmentServiceBuilder UseRepositoriesFromConfiguration(
        IConfiguration configuration)
    {
        return !string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"])
            ? UseMongoRepositories()
            : UseCosmosRepositories();
    }

    /// <summary>
    /// Wrap ITenantEnrollmentConfigRepository with a read-through cache
    /// backed by the shared <see cref="ICacheProvider"/>.
    ///
    /// Call AFTER UseCosmosRepositories() or UseMongoRepositories(). The
    /// host service must register <see cref="ICacheProvider"/> via
    /// <c>AddChoCaching(IConfiguration, IHostEnvironment)</c> before this
    /// method resolves — benefit-plan-service and fhir-service both do.
    ///
    /// The decorator re-registers ITenantEnrollmentConfigRepository,
    /// replacing the raw Cosmos/Mongo binding with the cache-wrapped
    /// version. The concrete Cosmos/Mongo type remains registered for the
    /// decorator to resolve as its inner dependency.
    /// </summary>
    public ProviderEnrollmentServiceBuilder WithTenantConfigCache()
    {
        _services.AddScoped<ITenantEnrollmentConfigRepository>(sp =>
        {
            ITenantEnrollmentConfigRepository inner =
                (ITenantEnrollmentConfigRepository?)
                    sp.GetService<TenantEnrollmentConfigRepositoryCosmos>()
             ?? sp.GetService<TenantEnrollmentConfigRepositoryMongo>()
             ?? throw new InvalidOperationException(
                    "WithTenantConfigCache() must be called after " +
                    "UseCosmosRepositories() or UseMongoRepositories().");

            return new RedisTenantEnrollmentConfigRepository(
                inner,
                sp.GetRequiredService<ICacheProvider>(),
                sp.GetRequiredService<IOptions<ProviderEnrollmentOptions>>(),
                sp.GetRequiredService<ILogger<RedisTenantEnrollmentConfigRepository>>());
        });

        return this;
    }

    /// <summary>
    /// Deprecated alias for <see cref="WithTenantConfigCache"/>. Backend is
    /// now selected by <c>AddChoCaching</c>, not by this extension method.
    /// Kept for one release so host services can migrate without coupling
    /// the rename to the ICacheProvider cutover.
    /// </summary>
    [Obsolete("Renamed to WithTenantConfigCache — the backend (Redis / InMemory / Null) is selected by AddChoCaching.")]
    public ProviderEnrollmentServiceBuilder WithRedisTenantConfigCache() =>
        WithTenantConfigCache();

    // ── State source registration ─────────────────────────────────

    public ProviderEnrollmentServiceBuilder WithTexasSource()
    {
        _services.AddHttpClient<TmhpPemsSource>((sp, c) =>
        {
            var opts = sp.GetRequiredService<IOptions<ProviderEnrollmentOptions>>().Value;
            c.BaseAddress = new Uri(opts.Tmhp.BaseUrl);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddStandardResilienceHandler();

        _services.AddScoped<IStateEnrollmentSource, TmhpPemsSource>();
        return this;
    }

    public ProviderEnrollmentServiceBuilder WithCaliforniaSource()
    {
        _services.AddHttpClient<DhcsPaveSource>(c =>
        {
            c.BaseAddress = new Uri("https://medi-cal.ca.gov/api/pave");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddStandardResilienceHandler();

        _services.AddScoped<IStateEnrollmentSource, DhcsPaveSource>();
        return this;
    }

    public ProviderEnrollmentServiceBuilder WithFloridaSource()
    {
        _services.AddHttpClient<AhcaFmmisSource>(c =>
        {
            c.BaseAddress = new Uri("https://www.floridamedicaid.com/api");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddStandardResilienceHandler();

        _services.AddScoped<IStateEnrollmentSource, AhcaFmmisSource>();
        return this;
    }

    public ProviderEnrollmentServiceBuilder WithNewYorkSource()
    {
        _services.AddHttpClient<EMedNySource>(c =>
        {
            c.BaseAddress = new Uri("https://www.emedny.org/api");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddStandardResilienceHandler();

        _services.AddScoped<IStateEnrollmentSource, EMedNySource>();
        return this;
    }

    /// <summary>
    /// Register CAQH ProView. OrganizationId is read from
    /// ProviderEnrollmentOptions.Caqh.OrganizationId (appsettings).
    /// </summary>
    public ProviderEnrollmentServiceBuilder WithCaqhSource()
    {
        _services.AddHttpClient<CaqhProViewSource>((sp, c) =>
        {
            var opts = sp.GetRequiredService<IOptions<ProviderEnrollmentOptions>>().Value;
            c.BaseAddress = new Uri(opts.Caqh.BaseUrl);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }).AddStandardResilienceHandler();

        _services.AddScoped<IStateEnrollmentSource, CaqhProViewSource>();
        return this;
    }

    public ProviderEnrollmentServiceBuilder WithAllStateSources() =>
        WithTexasSource()
            .WithCaliforniaSource()
            .WithFloridaSource()
            .WithNewYorkSource()
            .WithCaqhSource();

    // ── Lifecycle workers ─────────────────────────────────────────

    public ProviderEnrollmentServiceBuilder WithNightlyBatchSync()
    {
        _services.AddHostedService<NightlyBatchSyncWorker>();
        return this;
    }

    /// <summary>Requires IEnrollmentNotificationHandler registered by the host.</summary>
    public ProviderEnrollmentServiceBuilder WithRevalidationAlerts()
    {
        _services.AddHostedService<RevalidationAlertEngine>();
        return this;
    }
}
