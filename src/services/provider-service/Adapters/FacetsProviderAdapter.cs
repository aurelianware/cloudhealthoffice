using ProviderService.Models;

namespace ProviderService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub adapter for tenants whose provider directory lives in TriZetto Facets.
/// All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the payer-specific Facets connector is implemented and accepted.
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
