using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IOrganizationRepository"/> with full
/// version-chain semantics. Mirrors <see cref="InMemoryProviderRepository"/>:
/// every store and fetch round-trips through JSON so callers can mutate
/// returned objects without corrupting the stored copy.
/// </summary>
public sealed class InMemoryOrganizationRepository : IOrganizationRepository
{
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);
    private readonly List<Organization> _docs = new();
    public IReadOnlyList<Organization> Docs => _docs;

    /// <summary>Tenant context defaults to "tenant-a" for the service tests.</summary>
    public string TenantId { get; set; } = "tenant-a";

    private static Organization Clone(Organization org)
        => JsonSerializer.Deserialize<Organization>(JsonSerializer.Serialize(org, _jsonOpts), _jsonOpts)!;

    private static Organization Hydrate(Organization org)
    {
        if (string.IsNullOrEmpty(org.OrganizationId))
        {
            org.OrganizationId = org.Id;
        }
        if (string.IsNullOrEmpty(org.VersionId))
        {
            org.VersionId = org.Id;
            org.VersionNumber = org.VersionNumber <= 0 ? 1 : org.VersionNumber;
            org.VersionState = org.Status switch
            {
                OrganizationStatus.Terminated => OrganizationVersionState.Terminated,
                OrganizationStatus.Inactive => OrganizationVersionState.Suspended,
                OrganizationStatus.Pending => OrganizationVersionState.Draft,
                _ => OrganizationVersionState.Active
            };
        }
        org.Status = org.VersionState switch
        {
            OrganizationVersionState.Active => OrganizationStatus.Active,
            OrganizationVersionState.Suspended => OrganizationStatus.Inactive,
            OrganizationVersionState.Terminated => OrganizationStatus.Terminated,
            OrganizationVersionState.Superseded => OrganizationStatus.Inactive,
            OrganizationVersionState.Draft => OrganizationStatus.Pending,
            _ => org.Status
        };
        return org;
    }

    private IEnumerable<Organization> HydratedView()
        => _docs.Select(d => Hydrate(Clone(d)));

    public Task<Organization?> GetByIdAsync(string organizationId)
    {
        var match = HydratedView()
            .Where(o => (o.OrganizationId == organizationId || o.Id == organizationId)
                && o.TenantId == TenantId
                && o.VersionState != OrganizationVersionState.Draft)
            .OrderByDescending(o => o.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<Organization?>(match);
    }

    public Task<Organization?> GetVersionAsync(string organizationId, string versionId)
    {
        var match = HydratedView().FirstOrDefault(o =>
            (o.OrganizationId == organizationId || o.Id == organizationId)
            && o.TenantId == TenantId
            && o.VersionId == versionId);
        return Task.FromResult<Organization?>(match);
    }

    public Task<Organization?> GetLatestActiveAsync(string organizationId, DateTime asOf)
    {
        var match = HydratedView()
            .Where(o => (o.OrganizationId == organizationId || o.Id == organizationId)
                && o.TenantId == TenantId
                && o.VersionState == OrganizationVersionState.Active
                && (o.TerminationDate == null || o.TerminationDate >= asOf))
            .OrderByDescending(o => o.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<Organization?>(match);
    }

    public Task<(IReadOnlyList<Organization> Items, string? ContinuationToken)> ListVersionsAsync(
        string organizationId, int pageSize, string? continuationToken)
    {
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
            skip = parsed;

        var ordered = HydratedView()
            .Where(o => (o.OrganizationId == organizationId || o.Id == organizationId)
                && o.TenantId == TenantId)
            .OrderByDescending(o => o.VersionNumber)
            .Skip(skip)
            .ToList();

        var slice = ordered.Take(pageSize).ToList();
        var next = ordered.Count > pageSize ? (skip + pageSize).ToString() : null;
        return Task.FromResult<(IReadOnlyList<Organization>, string?)>((slice, next));
    }

    public Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType,
        LineOfBusiness? lineOfBusiness,
        string? parentOrganizationId,
        int page,
        int pageSize)
    {
        var heads = HydratedView()
            .Where(o => o.TenantId == TenantId && o.VersionState != OrganizationVersionState.Draft)
            .GroupBy(o => o.OrganizationId)
            .Select(g => g.OrderByDescending(o => o.VersionNumber).First())
            .ToList();

        if (networkType.HasValue)
            heads = heads.Where(o => o.NetworkType == networkType.Value).ToList();
        if (lineOfBusiness.HasValue)
            heads = heads.Where(o => o.LineOfBusiness == lineOfBusiness.Value).ToList();
        if (!string.IsNullOrEmpty(parentOrganizationId))
            heads = heads.Where(o => o.ParentOrganizationId == parentOrganizationId).ToList();

        heads = heads.OrderBy(o => o.Name).ToList();
        var total = heads.Count;
        var paged = heads.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult<(IReadOnlyList<Organization>, int?)>((paged, total));
    }

    public async Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId)
    {
        var (items, _) = await ListAsync(null, null, parentOrganizationId, 1, 500);
        return items;
    }

    public Task<Organization> CreateDraftAsync(Organization draft)
    {
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.OrganizationId)) draft.OrganizationId = draft.Id;
        if (string.IsNullOrEmpty(draft.TenantId)) draft.TenantId = TenantId;
        draft.VersionState = OrganizationVersionState.Draft;
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
    }

    public Task<Organization> UpdateDraftAsync(Organization draft)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == draft.Id && d.TenantId == draft.TenantId)
            ?? throw new OrganizationVersionStateException(draft.OrganizationId, draft.VersionId, OrganizationVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        if (existing.VersionState != OrganizationVersionState.Draft)
            throw new OrganizationVersionStateException(existing.OrganizationId, existing.VersionId, existing.VersionState,
                $"Organization version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        _docs.Remove(existing);
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
    }

    public Task<Organization> ActivateAndSupersedeAsync(Organization draftToActivate, Organization? predecessor)
    {
        var existingDraft = _docs.FirstOrDefault(d => d.Id == draftToActivate.Id);
        if (existingDraft != null) _docs.Remove(existingDraft);
        _docs.Add(Clone(draftToActivate));

        if (predecessor != null)
        {
            var existingPred = _docs.FirstOrDefault(d => d.Id == predecessor.Id);
            if (existingPred != null) _docs.Remove(existingPred);
            _docs.Add(Clone(predecessor));
        }
        return Task.FromResult(Clone(draftToActivate));
    }

    public Task<Organization> ReplaceVersionRowAsync(Organization version)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == version.Id && d.TenantId == version.TenantId);
        if (existing != null) _docs.Remove(existing);
        _docs.Add(Clone(version));
        return Task.FromResult(Clone(version));
    }
}
