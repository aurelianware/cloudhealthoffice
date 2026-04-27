using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Stub adapter for tenants whose provider directory lives in HealthEdge
/// HealthRules Payer. All methods throw <see cref="NotImplementedException"/>
/// with a clear migration TODO until the HealthEdge integration ships.
/// </summary>
/// <remarks>
/// TODO(healthedge-provider): integrate with the HealthRules Payer provider
/// inquiry API (HRP REST surface). Reference doc:
/// docs/architecture/provider-adapter-pattern.md.
/// </remarks>
public class HealthEdgeProviderAdapter : IProviderAdapter
{
    private const string Todo =
        "HealthEdge provider adapter not yet implemented. " +
        "TODO(healthedge-provider): integrate with the HealthRules Payer provider inquiry API. " +
        "See docs/architecture/provider-adapter-pattern.md.";

    public string Platform => "healthedge";

    public Task<ProviderAdapterResponse> GetProviderAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ProviderAdapterResponse> GetProviderByNpiAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<NetworkAdapterResponse> GetNetworkAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ProviderRosterAdapterResponse> GetNetworkRosterAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ProviderRosterAdapterResponse> SearchProvidersAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
