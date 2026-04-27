using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Adapters;

/// <summary>
/// Default provider adapter using CHO's internal <see cref="IProviderRepository"/>.
/// Preserves existing behavior — for the current set of tenants the factory
/// always resolves to this adapter and the read paths return the same rows
/// (post-hydration) the controller served before the refactor.
/// </summary>
/// <remarks>
/// Provider verification (<c>ProviderVerificationOrchestrator</c>) is intentionally
/// not wired into the adapter. Verification surfacing is the dedicated work of
/// capability 5.10 (Integrity Score Surface), which decorates provider responses
/// with cached integrity projections through a separate seam — adapter-agnostic,
/// so the same enrichment works equally for CHO/QNXT/Facets sources later.
/// </remarks>
public class ChoProviderAdapter : IProviderAdapter
{
    private const string NetworkPlaceholderTodo =
        "GetNetworkAsync is a placeholder — the Network entity arrives in capability 5.3. " +
        "TODO(provider-network-5.3): implement once the Network model + repository land. " +
        "See docs/architecture/provider-adapter-pattern.md.";

    private readonly IProviderRepository _repository;
    private readonly ILogger<ChoProviderAdapter> _logger;

    public string Platform => "cho";

    public ChoProviderAdapter(
        IProviderRepository repository,
        ILogger<ChoProviderAdapter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<ProviderAdapterResponse> GetProviderAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.ProviderId))
        {
            throw new ArgumentException(
                "ProviderId is required for GetProviderAsync.", nameof(request));
        }

        // Honor an explicit VersionId when supplied, otherwise return the
        // latest non-Draft head — the same behavior the legacy controller had.
        Provider? provider;
        if (!string.IsNullOrEmpty(request.VersionId))
        {
            provider = await _repository.GetVersionAsync(request.ProviderId, request.VersionId);
        }
        else
        {
            provider = await _repository.GetByIdAsync(request.ProviderId);
        }

        return new ProviderAdapterResponse
        {
            Platform = Platform,
            Provider = provider is null ? null : AdapterProvider.From(provider),
        };
    }

    public async Task<ProviderAdapterResponse> GetProviderByNpiAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.Npi))
        {
            throw new ArgumentException(
                "Npi is required for GetProviderByNpiAsync.", nameof(request));
        }

        var provider = await _repository.GetByNPIAsync(request.Npi);
        return new ProviderAdapterResponse
        {
            Platform = Platform,
            Provider = provider is null ? null : AdapterProvider.From(provider),
        };
    }

    public Task<NetworkAdapterResponse> GetNetworkAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(NetworkPlaceholderTodo);

    public async Task<ProviderRosterAdapterResponse> GetNetworkRosterAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
    {
        // Until 5.3 lands the Network entity, "roster" is sourced from the
        // existing provider rows whose NetworkParticipations satisfy the
        // requested plan/LOB/network scope. The repository's SearchAsync
        // already pushes plan/LOB filters down to the data store.
        var providers = await _repository.SearchAsync(
            name: request.Name,
            specialty: request.Specialty,
            zipCode: request.ZipCode,
            state: request.State,
            planId: request.PlanId,
            lineOfBusiness: request.LineOfBusiness,
            providerType: request.ProviderType,
            acceptingNewPatients: request.AcceptingNewPatients,
            page: request.Page,
            pageSize: request.PageSize);

        return new ProviderRosterAdapterResponse
        {
            Platform = Platform,
            Providers = providers.Select(AdapterProvider.From).ToList(),
        };
    }

    public async Task<ProviderRosterAdapterResponse> SearchProvidersAsync(
        ProviderAdapterRequest request, CancellationToken ct = default)
    {
        var providers = await _repository.SearchAsync(
            name: request.Name,
            specialty: request.Specialty,
            zipCode: request.ZipCode,
            state: request.State,
            planId: request.PlanId,
            lineOfBusiness: request.LineOfBusiness,
            providerType: request.ProviderType,
            acceptingNewPatients: request.AcceptingNewPatients,
            page: request.Page,
            pageSize: request.PageSize);

        return new ProviderRosterAdapterResponse
        {
            Platform = Platform,
            Providers = providers.Select(AdapterProvider.From).ToList(),
        };
    }
}
