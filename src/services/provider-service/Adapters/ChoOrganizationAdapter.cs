using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Adapters;

/// <summary>
/// Default <see cref="IOrganizationAdapter"/> using CHO's internal
/// <see cref="IOrganizationRepository"/>. For every tenant currently in
/// production the factory resolves to this adapter and reads the rows
/// the repository returns directly (post-hydration), with no translation.
/// </summary>
public class ChoOrganizationAdapter : IOrganizationAdapter
{
    private readonly IOrganizationRepository _repository;
    private readonly ILogger<ChoOrganizationAdapter> _logger;

    public string Platform => "cho";

    public ChoOrganizationAdapter(
        IOrganizationRepository repository,
        ILogger<ChoOrganizationAdapter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<OrganizationAdapterResponse> GetOrganizationAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.OrganizationId))
        {
            throw new ArgumentException(
                "OrganizationId is required for GetOrganizationAsync.", nameof(request));
        }

        Organization? org;
        if (!string.IsNullOrEmpty(request.VersionId))
        {
            org = await _repository.GetVersionAsync(request.OrganizationId, request.VersionId);
        }
        else
        {
            org = await _repository.GetByIdAsync(request.OrganizationId);
        }

        return new OrganizationAdapterResponse
        {
            Platform = Platform,
            Organization = org is null ? null : AdapterOrganization.From(org),
        };
    }

    public async Task<OrganizationListAdapterResponse> GetByParentAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.ParentOrganizationId))
        {
            throw new ArgumentException(
                "ParentOrganizationId is required for GetByParentAsync.", nameof(request));
        }

        var children = await _repository.GetByParentAsync(request.ParentOrganizationId);
        return new OrganizationListAdapterResponse
        {
            Platform = Platform,
            Organizations = children.Select(AdapterOrganization.From).ToList(),
            TotalCount = children.Count,
        };
    }

    public async Task<OrganizationListAdapterResponse> ListAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
    {
        var (items, total) = await _repository.ListAsync(
            networkType: request.NetworkType,
            lineOfBusiness: request.LineOfBusiness,
            parentOrganizationId: request.ParentOrganizationId,
            page: request.Page,
            pageSize: request.PageSize);

        return new OrganizationListAdapterResponse
        {
            Platform = Platform,
            Organizations = items.Select(AdapterOrganization.From).ToList(),
            TotalCount = total,
        };
    }
}
