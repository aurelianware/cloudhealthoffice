using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Stub adapter for tenants whose provider directory lives in QNXT
/// (TriZetto / Cognizant). All methods throw <see cref="NotImplementedException"/>
/// with a clear migration TODO until the QNXT integration ships.
/// </summary>
/// <remarks>
/// TODO(qnxt-provider): integrate with the QNXT provider directory API
/// (PROVIDER_INQ on the QNXT provider stack). Reference doc:
/// docs/architecture/provider-adapter-pattern.md.
/// </remarks>
public class QnxtProviderAdapter : IProviderAdapter
{
    private const string Todo =
        "QNXT provider adapter not yet implemented. " +
        "TODO(qnxt-provider): integrate with the QNXT provider directory API. " +
        "See docs/architecture/provider-adapter-pattern.md.";

    public string Platform => "qnxt";

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
