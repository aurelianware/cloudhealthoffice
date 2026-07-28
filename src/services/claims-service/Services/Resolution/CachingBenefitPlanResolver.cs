using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="IBenefitPlanResolver"/> with a 5-minute
/// in-process TTL keyed by <c>(tenantId, planId)</c>. Mirrors the
/// BP 5.6 <see cref="BenefitPlanService.Repositories.CachingServiceCategoryMappingRepository"/>
/// shape: read-through cache, no distributed invalidation, coherence
/// across pods relies on TTL expiry.
///
/// <para>
/// The pipeline calls <see cref="IBenefitPlanResolver.GetPlanAsync"/>
/// once per claim. Tenants typically have a small set of active plans
/// reused across thousands of claims, so the cache hit rate sits high
/// and benefit-plan-service load drops by an order of magnitude under
/// realistic claim volumes.
/// </para>
///
/// <para>
/// Negative results (resolver returned null) are not cached — a
/// transient benefit-plan-service outage shouldn't pin "missing" for
/// the full TTL window. Subsequent requests retry the live call.
/// </para>
/// </summary>
public sealed class CachingBenefitPlanResolver : IBenefitPlanResolver
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly IBenefitPlanResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Lazy<Task<ResolvedBenefitPlan?>>> _inflight = new();

    public CachingBenefitPlanResolver(IBenefitPlanResolver inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingBenefitPlanResolver(IBenefitPlanResolver inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<ResolvedBenefitPlan?> GetPlanAsync(
        string tenantId, string planId, CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, planId);
        if (_cache.TryGetValue<ResolvedBenefitPlan>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        // A newly published plan causes many claims to miss together. Without
        // single-flight coalescing, every miss reaches benefit-plan-service
        // and can exhaust a Cosmos free-tier RU budget before the first result
        // populates the cache.
        var candidate = new Lazy<Task<ResolvedBenefitPlan?>>(
            () => ResolveAndCacheAsync(key, tenantId, planId, ct),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var inflight = _inflight.GetOrAdd(key, candidate);

        try
        {
            return await inflight.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (_inflight.TryGetValue(key, out var current)
                && ReferenceEquals(current, inflight))
            {
                _inflight.TryRemove(key, out _);
            }
        }
    }

    private async Task<ResolvedBenefitPlan?> ResolveAndCacheAsync(
        string key,
        string tenantId,
        string planId,
        CancellationToken ct)
    {
        // A previous flight may have filled the cache between the caller's
        // initial check and this lazy operation beginning.
        if (_cache.TryGetValue<ResolvedBenefitPlan>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var fresh = await _inner.GetPlanAsync(tenantId, planId, ct).ConfigureAwait(false);
        if (fresh is not null && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(string tenantId, string planId) =>
        $"benefitplan:{tenantId}:{planId}";
}
