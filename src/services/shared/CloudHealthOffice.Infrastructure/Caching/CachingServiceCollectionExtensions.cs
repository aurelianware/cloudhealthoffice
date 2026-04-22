using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Registers <see cref="ICacheProvider"/> and resolves the backend from
/// configuration + environment. Mirrors
/// <c>AddChoMessaging</c> — same Auto/InMemory/Production resolution logic,
/// same soft deprecation of the legacy <c>Redis:ConnectionString</c> key.
/// </summary>
public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ICacheProvider"/> as a singleton.
    ///
    /// Backend resolution:
    ///   <c>Auto</c>     — prod + connection string present → Redis;
    ///                      otherwise InMemory (with a warning if prod).
    ///   <c>Redis</c>    — forced; throws if connection string missing.
    ///   <c>InMemory</c> — forced.
    ///   <c>Null</c>     — forced no-op.
    ///
    /// Config keys: <c>Caching:Backend</c>, <c>Caching:RedisConnectionString</c>.
    ///
    /// Back-compat: <c>Redis:ConnectionString</c> is honoured as a fallback
    /// and emits a single deprecation warning at startup. Follow-up: remove
    /// this fallback after one release cycle.
    ///
    /// Safe to call after an <c>AddSingleton&lt;IConnectionMultiplexer&gt;</c>
    /// registration — the host service (e.g. benefit-plan-service) keeps its
    /// direct multiplexer for callers that need Redis-native semantics
    /// (<c>RedisAccumulatorService</c>, <c>RedisPaRuleRepository</c>), and
    /// this method layers <c>ICacheProvider</c> on top of the same physical
    /// Redis instance.
    /// </summary>
    public static IServiceCollection AddChoCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new CachingOptions();
        configuration.GetSection(CachingOptions.SectionName).Bind(options);

        var (connectionString, deprecatedKey) = ResolveConnectionString(configuration, options);
        options.RedisConnectionString = connectionString;
        services.AddSingleton(options);

        var decision = ResolveBackend(options, environment);

        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        // Capture the passed-in IHostEnvironment so callers (tests, or hosts
        // that build a ServiceCollection without a HostBuilder) don't have
        // to separately register IHostEnvironment in DI for CacheKeyGuard.
        services.AddSingleton<CacheKeyGuard>(sp =>
            new CacheKeyGuard(sp.GetRequiredService<IHttpContextAccessor>(), environment));
        services.AddSingleton<SingleFlightRunner>(_ =>
            new SingleFlightRunner(options.SingleFlightMaxInFlight));

        // On the Redis backend path, ensure IConnectionMultiplexer is
        // registered in DI so callers that hold a deliberate direct
        // dependency (RedisAccumulatorService, RedisPaRuleRepository's
        // SCAN flush) can inject it without the host having to open a
        // second connection. TryAdd so hosts that pre-register their own
        // multiplexer (benefit-plan-service) win.
        if (decision.Backend == CachingBackend.Redis)
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(options.RedisConnectionString!));
        }

        services.AddSingleton<ICacheProvider>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger        = loggerFactory.CreateLogger("CloudHealthOffice.Infrastructure.Caching");

            logger.LogInformation(
                "ICacheProvider={Backend} ({Reason})",
                decision.Backend, decision.Reason);

            if (decision.Backend == CachingBackend.InMemory &&
                !environment.IsDevelopment() &&
                string.Equals((options.Backend ?? "Auto").Trim(), "Auto",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Same reasoning as AddChoMessaging: Auto → InMemory outside
                // Development is almost always a misconfig. A process-local
                // cache doesn't share state across replicas, so
                // invalidations don't propagate and TTLs are inconsistent.
                logger.LogWarning(
                    "ICacheProvider resolved to InMemory in {Environment}. " +
                    "Configure Caching:RedisConnectionString for cross-replica cache coherence.",
                    environment.EnvironmentName);
            }

            if (deprecatedKey is not null)
            {
                logger.LogWarning(
                    "Config key '{Deprecated}' is deprecated; migrate to '{Canonical}'. " +
                    "Falling back for this release.",
                    deprecatedKey, "Caching:RedisConnectionString");
            }

            ICacheProvider inner = decision.Backend switch
            {
                CachingBackend.Redis => BuildRedisProvider(sp, options, loggerFactory),
                CachingBackend.Null  => new NullCacheProvider(),
                _                     => new InMemoryCacheProvider(
                                             sp.GetRequiredService<IMemoryCache>(),
                                             sp.GetRequiredService<SingleFlightRunner>())
            };

            return new GuardedCacheProvider(inner, sp.GetRequiredService<CacheKeyGuard>());
        });

        return services;
    }

    private static ICacheProvider BuildRedisProvider(
        IServiceProvider sp,
        CachingOptions options,
        ILoggerFactory loggerFactory)
    {
        // The multiplexer is already in DI (via TryAddSingleton above or a
        // host pre-registration). Resolve it so every Redis touchpoint in
        // the process shares one connection.
        return new RedisCacheProvider(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<SingleFlightRunner>(),
            loggerFactory.CreateLogger<RedisCacheProvider>());
    }

    internal static (string? ConnectionString, string? DeprecatedKey) ResolveConnectionString(
        IConfiguration configuration, CachingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
            return (options.RedisConnectionString, null);

        // Legacy fallback: Redis:ConnectionString is where benefit-plan-service
        // and fhir-service currently read the string from. Honour it so the
        // refactor lands without a config migration, and flag it so the next
        // release can drop it.
        var legacy = configuration["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(legacy))
            return (legacy, "Redis:ConnectionString");

        return (null, null);
    }

    internal static BackendDecision ResolveBackend(
        CachingOptions options, IHostEnvironment environment)
    {
        var backend = (options.Backend ?? "Auto").Trim();
        var hasCs   = !string.IsNullOrWhiteSpace(options.RedisConnectionString);

        return backend.ToLowerInvariant() switch
        {
            "redis" when hasCs => new BackendDecision(
                CachingBackend.Redis,
                $"forced; env={environment.EnvironmentName}"),
            "redis" => throw new InvalidOperationException(
                "Caching:Backend=Redis requires Caching:RedisConnectionString (or the legacy Redis:ConnectionString fallback)."),
            "inmemory" => new BackendDecision(
                CachingBackend.InMemory,
                $"forced; env={environment.EnvironmentName}"),
            "null" => new BackendDecision(
                CachingBackend.Null,
                $"forced; env={environment.EnvironmentName}"),
            "auto" or "" when environment.IsDevelopment() => new BackendDecision(
                CachingBackend.InMemory,
                "Auto; env=Development"),
            "auto" or "" when hasCs => new BackendDecision(
                CachingBackend.Redis,
                $"Auto; ConnectionString configured; env={environment.EnvironmentName}"),
            "auto" or "" => new BackendDecision(
                CachingBackend.InMemory,
                $"Auto; no ConnectionString; env={environment.EnvironmentName}"),
            _ => throw new InvalidOperationException(
                $"Caching:Backend='{options.Backend}' is not recognised. Use Auto, Redis, InMemory, or Null.")
        };
    }

    internal enum CachingBackend { Auto, Redis, InMemory, Null }
    internal record BackendDecision(CachingBackend Backend, string Reason);
}
