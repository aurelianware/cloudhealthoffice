using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Rules;
using CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StackExchange.Redis;

namespace CloudHealthOffice.PriorAuthRuleEngine.Configuration;

/// <summary>
/// DI registration for CloudHealthOffice.PriorAuthRuleEngine.
///
/// ── Typical usage ─────────────────────────────────────────────────────────
///
/// // fhir-service / authorization-service Program.cs:
/// builder.Services
///     .AddPriorAuthRuleEngine(builder.Configuration)
///     .UseCosmosRepository()
///     .WithRedisRuleCache()
///     .WithPlatformRules()
///     .SeedOnStartup();        // seeds TX platform rules if not already present
///
/// // Wire into PasAutoAdjudicator manually after registration:
/// //   inject IPriorAuthRuleEngine → call EvaluateAsync() as Rule 5
///
/// ── appsettings.json additions ────────────────────────────────────────────
///
/// "PriorAuthRuleEngine": {
///   "RuleSetCacheTtl": "00:15:00",
///   "GoldCardLookbackDays": 180,
///   "PendOnRuleError": true,
///   "RulesContainer": "prior-auth-rules",       // Cosmos
///   "RulesCollection": "prior_auth_rules"        // Mongo
/// }
/// </summary>
public static class PriorAuthRuleEngineServiceCollectionExtensions
{
    public static PriorAuthRuleEngineBuilder AddPriorAuthRuleEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PriorAuthRuleEngineOptions>(
            configuration.GetSection(PriorAuthRuleEngineOptions.SectionName));

        services.AddScoped<IPriorAuthRuleEngine, PriorAuthRuleEngineService>();

        return new PriorAuthRuleEngineBuilder(services);
    }
}

public sealed class PriorAuthRuleEngineBuilder
{
    private readonly IServiceCollection _services;

    internal PriorAuthRuleEngineBuilder(IServiceCollection services)
    {
        _services = services;
    }

    // ── Persistence ───────────────────────────────────────────────

    /// <summary>
    /// Use Cosmos DB for rule storage.
    /// Requires CosmosClient to be registered by the host.
    /// Container: prior-auth-rules, partition key: /stateCode
    /// </summary>
    public PriorAuthRuleEngineBuilder UseCosmosRepository()
    {
        _services.AddScoped<PaRuleRepositoryCosmos>();
        _services.AddScoped<IPaRuleRepository>(sp =>
            sp.GetRequiredService<PaRuleRepositoryCosmos>());
        return this;
    }

    /// <summary>
    /// Use MongoDB for rule storage.
    /// Requires IMongoDatabase to be registered by the host.
    /// Collection: prior_auth_rules — indexes created automatically.
    /// </summary>
    public PriorAuthRuleEngineBuilder UseMongoRepository()
    {
        _services.AddScoped<PaRuleRepositoryMongo>();
        _services.AddScoped<IPaRuleRepository>(sp =>
            sp.GetRequiredService<PaRuleRepositoryMongo>());
        return this;
    }

    /// <summary>
    /// Auto-detect from configuration (MongoDb:ConnectionString → Mongo, else Cosmos).
    /// </summary>
    public PriorAuthRuleEngineBuilder UseRepositoryFromConfiguration(IConfiguration configuration)
    {
        return !string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"])
            ? UseMongoRepository()
            : UseCosmosRepository();
    }

    /// <summary>
    /// Add a read-through cache in front of the rule repository.
    /// Call AFTER UseCosmosRepository() or UseMongoRepository().
    ///
    /// Requires:
    ///   • <see cref="ICacheProvider"/> + <see cref="CacheKeyGuard"/> —
    ///     for K/V reads, writes, and exact-key invalidation. Registered
    ///     by the host via <c>AddChoCaching(IConfiguration, IHostEnvironment)</c>.
    ///   • <see cref="IConnectionMultiplexer"/> — OPTIONAL. Used by the
    ///     state-level <c>SCAN</c> path on
    ///     <see cref="RedisPaRuleRepository.DeleteAsync"/>. Registered
    ///     automatically by <c>AddChoCaching</c> when the cache backend
    ///     resolves to Redis; absent when it resolves to InMemory / Null.
    ///     When absent, the state-flush degrades to a debug log and
    ///     InMemory entries rely on TTL. Exact-key invalidation on
    ///     Upsert / BulkUpsert continues to work via
    ///     <see cref="ICacheProvider"/>.
    ///
    /// Cache key: pa-rules:{stateCode}:{lob}:{program}:{tenantId}
    /// Default TTL: 15 minutes (PriorAuthRuleEngineOptions.RuleSetCacheTtl)
    /// Invalidated on every UpsertAsync, BulkUpsertAsync, and DeleteAsync.
    /// </summary>
    public PriorAuthRuleEngineBuilder WithRuleCache()
    {
        _services.AddScoped<IPaRuleRepository>(sp =>
        {
            IPaRuleRepository inner =
                (IPaRuleRepository?)sp.GetService<PaRuleRepositoryCosmos>()
             ?? sp.GetService<PaRuleRepositoryMongo>()
             ?? throw new InvalidOperationException(
                    "WithRuleCache() must be called after UseCosmosRepository() " +
                    "or UseMongoRepository().");

            return new RedisPaRuleRepository(
                inner,
                sp.GetRequiredService<ICacheProvider>(),
                sp.GetService<IConnectionMultiplexer>(),
                sp.GetRequiredService<CacheKeyGuard>(),
                sp.GetRequiredService<IOptions<PriorAuthRuleEngineOptions>>(),
                sp.GetRequiredService<ILogger<RedisPaRuleRepository>>());
        });

        return this;
    }

    /// <summary>
    /// Deprecated alias for <see cref="WithRuleCache"/>. The backend is
    /// now selected by <c>AddChoCaching</c>, not by this method name.
    /// Kept for one release so host services can migrate the rename
    /// separately from the ICacheProvider cutover.
    /// </summary>
    [Obsolete("Renamed to WithRuleCache — the backend (Redis / InMemory / Null) is selected by AddChoCaching.")]
    public PriorAuthRuleEngineBuilder WithRedisRuleCache() => WithRuleCache();

    // ── Rule implementations ──────────────────────────────────────

    /// <summary>
    /// Register all platform-shipped rule implementations.
    /// These are the C# classes that execute rule documents.
    /// Adding a new rule type requires registering it here.
    /// </summary>
    public PriorAuthRuleEngineBuilder WithPlatformRules()
    {
        // Each rule is registered as IPaRule — the engine discovers all of them
        _services.AddSingleton<IPaRule, TxGoldCardExemptionRule>();
        _services.AddSingleton<IPaRule, ProcedureRequiresAuthRule>();
        _services.AddSingleton<IPaRule, QuantityLimitRule>();
        _services.AddSingleton<IPaRule, DiagnosisRequiredRule>();
        _services.AddSingleton<IPaRule, ProviderTypeExemptionRule>();
        _services.AddSingleton<IPaRule, MemberAgeLimitRule>();
        return this;
    }

    // ── Optional pre-fetch services ───────────────────────────────

    /// <summary>
    /// Register a custom provider approval history service.
    /// Required for gold card exemption rules.
    /// Implement IProviderApprovalHistoryService in the host service.
    /// </summary>
    public PriorAuthRuleEngineBuilder WithProviderHistory<T>()
        where T : class, IProviderApprovalHistoryService
    {
        _services.AddScoped<IProviderApprovalHistoryService, T>();
        return this;
    }

    /// <summary>
    /// Register a custom member auth history service.
    /// Required for quantity limit rules.
    /// Implement IMemberAuthHistoryService in the host service.
    /// </summary>
    public PriorAuthRuleEngineBuilder WithMemberHistory<T>()
        where T : class, IMemberAuthHistoryService
    {
        _services.AddScoped<IMemberAuthHistoryService, T>();
        return this;
    }

    // ── Seed data ─────────────────────────────────────────────────

    /// <summary>
    /// Seed platform TX Medicaid rules on first startup if not already present.
    /// Idempotent — checks by RuleId before writing.
    /// Safe to leave enabled in production.
    /// </summary>
    public PriorAuthRuleEngineBuilder SeedOnStartup()
    {
        _services.AddHostedService<PriorAuthRuleEngineSeeder>();
        return this;
    }
}

// ─────────────────────────────────────────────────────────────────
// Seeder — IHostedService, runs once at startup
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Seeds platform PA rules on first deployment.
/// Runs as IHostedService.StartAsync — completes before the host starts serving.
/// Idempotent: only writes rules whose RuleId does not already exist.
/// </summary>
internal sealed class PriorAuthRuleEngineSeeder : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PriorAuthRuleEngineSeeder> _logger;

    public PriorAuthRuleEngineSeeder(
        IServiceProvider sp,
        ILogger<PriorAuthRuleEngineSeeder> logger)
    {
        _sp     = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaRuleRepository>();

        var platformRules = TxMedicaidSeedRules.GetAll();
        var seeded        = 0;

        // Pre-fetch existing rules per state to avoid N+1 queries
        var existingRuleIdsByState = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in platformRules)
        {
            if (!existingRuleIdsByState.TryGetValue(rule.StateCode, out var existingRuleIds))
            {
                var existing = await repo.ListAsync(tenantId: null, stateCode: rule.StateCode, ct);
                existingRuleIds = new HashSet<string>(existing.Select(r => r.RuleId), StringComparer.OrdinalIgnoreCase);
                existingRuleIdsByState[rule.StateCode] = existingRuleIds;
            }

            if (existingRuleIds.Contains(rule.RuleId))
                continue;

            await repo.UpsertAsync(rule, ct);
            existingRuleIds.Add(rule.RuleId);
            seeded++;
            _logger.LogInformation("Seeded PA rule {RuleId}: {RuleName}", rule.RuleId, rule.RuleName);
        }

        if (seeded > 0)
            _logger.LogInformation("PriorAuthRuleEngine: seeded {Count} platform rules", seeded);
        else
            _logger.LogDebug("PriorAuthRuleEngine: all platform rules already present — no seeding required");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
