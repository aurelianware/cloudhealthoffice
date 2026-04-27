using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Stub adapter for tenants whose provider directory lives in TriZetto Facets.
/// All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the Facets integration ships.
/// </summary>
/// <remarks>
/// TODO(facets-provider): integrate with the Facets provider inquiry surface
/// (typically the Open Access XML interface or Facets Workflow service).
/// Reference doc: docs/architecture/provider-adapter-pattern.md.
/// </remarks>
public class FacetsProviderAdapter : IProviderAdapter
{
    private const string Todo =
        "Facets provider adapter not yet implemented. " +
        "TODO(facets-provider): integrate with the Facets provider inquiry interface. " +
        "See docs/architecture/provider-adapter-pattern.md.";

    public string Platform => "facets";

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
