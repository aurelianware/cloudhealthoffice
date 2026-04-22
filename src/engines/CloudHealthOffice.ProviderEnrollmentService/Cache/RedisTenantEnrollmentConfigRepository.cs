using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Read-through cache decorator for <see cref="ITenantEnrollmentConfigRepository"/>
/// built on the shared <see cref="ICacheProvider"/>.
///
/// ── Cache behaviour ───────────────────────────────────────────────
///
///   GetAsync:    ICacheProvider.GetOrSetAsync — hit returns cached
///                value, miss invokes inner repo and caches the result.
///                A null result from inner (tenant not configured) is
///                NOT cached so a newly seeded tenant document becomes
///                visible immediately.
///   UpsertAsync: inner write → RemoveAsync (invalidate).
///   DeleteAsync: inner delete → RemoveAsync (invalidate).
///   ListAsync:   always hits the inner repo (admin path — not cached).
///
/// ── Key layout ────────────────────────────────────────────────────
///
///   Logical key: <c>enrollment:config:{tenantId}</c>
///   CacheKeyGuard rewrites to <c>{env}:{tenantId}:enrollment:config:{tenantId}</c>
///   so cross-tenant collisions are structurally impossible.
///
/// ── Backend resilience ────────────────────────────────────────────
///
///   ICacheProvider swallows RedisException internally and degrades
///   to a miss / no-op. The gate continues to function — just with
///   database latency on every call rather than sub-millisecond
///   Redis reads.
/// </summary>
public sealed class RedisTenantEnrollmentConfigRepository : ITenantEnrollmentConfigRepository
{
    private readonly ITenantEnrollmentConfigRepository _inner;
    private readonly ICacheProvider _cache;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisTenantEnrollmentConfigRepository> _logger;

    public RedisTenantEnrollmentConfigRepository(
        ITenantEnrollmentConfigRepository inner,
        ICacheProvider cache,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<RedisTenantEnrollmentConfigRepository> logger)
    {
        _inner  = inner;
        _cache  = cache;
        _ttl    = options.Value.TenantConfigCacheTtl;
        _logger = logger;
    }

    public Task<TenantEnrollmentConfig?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        return _cache.GetOrSetAsync<TenantEnrollmentConfig>(
            MakeKey(tenantId),
            token => _inner.GetAsync(tenantId, token),
            _ttl,
            CacheScope.Tenant,
            ct);
    }

    public async Task UpsertAsync(TenantEnrollmentConfig config, CancellationToken ct = default)
    {
        await _inner.UpsertAsync(config, ct);
        await _cache.RemoveAsync(MakeKey(config.TenantId), CacheScope.Tenant, ct);
    }

    public async Task DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        await _inner.DeleteAsync(tenantId, ct);
        await _cache.RemoveAsync(MakeKey(tenantId), CacheScope.Tenant, ct);
    }

    public Task<IReadOnlyList<TenantEnrollmentConfig>> ListAsync(CancellationToken ct = default)
        => _inner.ListAsync(ct);

    private static string MakeKey(string tenantId) => $"enrollment:config:{tenantId}";
}
