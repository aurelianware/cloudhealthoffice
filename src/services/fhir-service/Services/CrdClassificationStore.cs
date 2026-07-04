using System.Collections.Concurrent;
using FhirService.Models;

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
    private readonly ConcurrentDictionary<string, CrdCodeClassification> _cache = new();

    /// <inheritdoc />
    public bool TryGet(string tenantId, out CrdCodeClassification? classification)
        => _cache.TryGetValue(tenantId, out classification);

    /// <inheritdoc />
    public void Set(string tenantId, CrdCodeClassification classification)
        => _cache[tenantId] = classification;

    /// <inheritdoc />
    public CrdCodeClassification? GetOrNull(string tenantId)
    {
        _cache.TryGetValue(tenantId, out var c);
        return c;
    }
}
