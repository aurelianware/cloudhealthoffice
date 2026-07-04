using FhirService.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FhirService.Services;

/// <summary>
/// Singleton store for per-tenant CRD code classifications.
/// Injected into <see cref="CrdService"/> so that the cache lifetime is
/// explicit and testable, and does not bleed across test cases via static state.
/// </summary>
public interface ICrdClassificationStore
{
    bool TryGet(string tenantId, out CrdCodeClassification? classification);
    void Set(string tenantId, CrdCodeClassification classification);
    CrdCodeClassification? GetOrNull(string tenantId);
}

/// <inheritdoc />
public sealed class CrdClassificationStore : ICrdClassificationStore
{
    private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromHours(6);
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromHours(1);

    private readonly IMemoryCache _cache;

    public CrdClassificationStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <inheritdoc />
    public bool TryGet(string tenantId, out CrdCodeClassification? classification)
        => _cache.TryGetValue(CacheKey(tenantId), out classification);

    /// <inheritdoc />
    public void Set(string tenantId, CrdCodeClassification classification)
    {
        _cache.Set(
            CacheKey(tenantId),
            classification,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = AbsoluteExpiration,
                SlidingExpiration = SlidingExpiration,
                Size = 1,
            });
    }

    /// <inheritdoc />
    public CrdCodeClassification? GetOrNull(string tenantId)
    {
        _cache.TryGetValue(CacheKey(tenantId), out CrdCodeClassification? c);
        return c;
    }

    private static string CacheKey(string tenantId) => $"crd-classification:{tenantId}";
}
