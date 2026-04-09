using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// Thread-safe in-memory fallback for <see cref="IComplianceConfigRepository"/>,
/// used when Cosmos DB is not configured (local dev without Azure credentials).
/// Data is scoped to the process lifetime — use only in development or testing.
/// </summary>
public class InMemoryComplianceConfigRepository : IComplianceConfigRepository
{
    private readonly Dictionary<string, TenantComplianceConfig> _store =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    public Task<TenantComplianceConfig?> GetAsync(string tenantId)
    {
        lock (_lock)
        {
            _store.TryGetValue(tenantId, out var config);
            return Task.FromResult(config);
        }
    }

    public Task<TenantComplianceConfig> UpsertAsync(TenantComplianceConfig config)
    {
        lock (_lock)
        {
            _store[config.TenantId] = config;
            return Task.FromResult(config);
        }
    }
}
