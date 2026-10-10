using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BenefitPlanService.HealthChecks;

/// <summary>
/// The accumulator Redis (<c>RedisAccumulatorService</c>) holds state that is not a
/// cache: the per-claim commit journal and the void / denial fences
/// (<c>__commit:*</c>, <c>__fence:*</c>). An evicted hash loses them — a late commit
/// of a denied claim would then land, and a re-adjudication could no longer undo its
/// earlier write. The keys carry a TTL (plan year + run-out), so any
/// <c>volatile-*</c> policy evicts them as readily as an <c>allkeys-*</c> one: only
/// <c>maxmemory-policy noeviction</c> is safe.
/// <para>Reads <c>INFO memory</c> (allowed on managed Redis, where <c>CONFIG</c> is
/// not). Another policy fails readiness unless
/// <c>BenefitEngine:Redis:AllowEvictingPolicy</c> is true (then Degraded); a policy
/// that cannot be read is Degraded. Every non-healthy result is logged as an
/// error.</para>
/// </summary>
public sealed class RedisAccumulatorEvictionHealthCheck : IHealthCheck
{
    public const string Name = "redis-accumulator-eviction-policy";
    public const string AllowEvictingPolicySetting = "BenefitEngine:Redis:AllowEvictingPolicy";

    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RedisAccumulatorEvictionHealthCheck> _logger;

    public RedisAccumulatorEvictionHealthCheck(
        IConnectionMultiplexer redis, IConfiguration configuration, ILogger<RedisAccumulatorEvictionHealthCheck> logger)
    {
        _redis = redis;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>The <c>maxmemory_policy</c> line of an <c>INFO memory</c> reply, or null.</summary>
    public static string? ParsePolicy(string? info) =>
        info?.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("maxmemory_policy:", StringComparison.Ordinal))?
            ["maxmemory_policy:".Length..]
            .Trim();

    /// <summary>Healthy for <c>noeviction</c>; otherwise Unhealthy, or Degraded when allowed.</summary>
    public static HealthCheckResult Evaluate(string? policy, bool allowEvicting)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return HealthCheckResult.Degraded("Could not read the Redis maxmemory-policy; accumulator journal and fences need noeviction.");
        if (string.Equals(policy, "noeviction", StringComparison.OrdinalIgnoreCase))
            return HealthCheckResult.Healthy("maxmemory-policy noeviction");
        var message = $"Redis maxmemory-policy is '{policy}': accumulator hashes (commit journal, void/denial fences) can be evicted. Set noeviction.";
        return allowEvicting ? HealthCheckResult.Degraded(message) : HealthCheckResult.Unhealthy(message);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        HealthCheckResult result;
        try
        {
            var info = (await _redis.GetDatabase().ExecuteAsync("INFO", "memory")).ToString();
            result = Evaluate(ParsePolicy(info), _configuration.GetValue(AllowEvictingPolicySetting, false));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            result = HealthCheckResult.Degraded("Could not read the Redis maxmemory-policy.", ex);
        }
        if (result.Status != HealthStatus.Healthy)
            _logger.LogError("Accumulator Redis eviction check: {Status} — {Description}", result.Status, result.Description);
        return result;
    }
}

/// <summary>Runs <see cref="RedisAccumulatorEvictionHealthCheck"/> once at startup so a wrong policy is logged at once.</summary>
public sealed class RedisAccumulatorEvictionStartupCheck(RedisAccumulatorEvictionHealthCheck check) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await check.CheckHealthAsync(new HealthCheckContext(), stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check logs its own failures; never take the host down from here.
        }
    }
}
