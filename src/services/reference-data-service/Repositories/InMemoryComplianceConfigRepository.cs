using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// Thread-safe in-memory <see cref="IComplianceConfigRepository"/>, used only as a
/// test double. Program.cs never registers it: compliance configuration lives in
/// MongoDB in every environment.
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
