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
///   "RuleSetCacheTtlMinutes": 15,
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
    /// Add a Redis read-through cache in front of the rule repository.
    /// Call AFTER UseCosmosRepository() or UseMongoRepository().
    /// Requires IConnectionMultiplexer registered by the host.
    ///
    /// Cache key: pa-rules:{stateCode}:{lob}:{program}:{tenantId}
    /// Default TTL: 15 minutes (PriorAuthRuleEngineOptions.RuleSetCacheTtl)
    /// Invalidated on every UpsertAsync and DeleteAsync.
    /// </summary>
    public PriorAuthRuleEngineBuilder WithRedisRuleCache()
    {
        _services.AddScoped<IPaRuleRepository>(sp =>
        {
            IPaRuleRepository inner =
                (IPaRuleRepository?)sp.GetService<PaRuleRepositoryCosmos>()
             ?? sp.GetService<PaRuleRepositoryMongo>()
             ?? throw new InvalidOperationException(
                    "WithRedisRuleCache() must be called after UseCosmosRepository() " +
                    "or UseMongoRepository().");

            return new RedisPaRuleRepository(
                inner,
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IOptions<PriorAuthRuleEngineOptions>>(),
                sp.GetRequiredService<ILogger<RedisPaRuleRepository>>());
        });

        return this;
    }

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

        foreach (var rule in platformRules)
        {
            // Check if already present (by state + ruleId)
            var existing = await repo.ListAsync(tenantId: null, stateCode: rule.StateCode, ct);
            if (existing.Any(r => r.RuleId == rule.RuleId))
                continue;

            await repo.UpsertAsync(rule, ct);
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
