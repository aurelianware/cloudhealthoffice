using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Lifecycle façade over <see cref="IOrganizationRepository"/>. Owns
/// identity assignment (ULID, version number, predecessor pointer),
/// state transitions, and amend / activate sequencing.
/// </summary>
/// <remarks>
/// CRUD on <c>NetworksController</c> maps as:
/// <list type="bullet">
///   <item><c>POST</c> → <see cref="CreateAndActivateAsync"/> — creates a
///         genesis Draft and immediately activates it.</item>
///   <item><c>PUT</c> → <see cref="UpdateAsync"/> — clones the current head
///         into a new Draft, applies the new field values, and activates it,
///         superseding the prior head.</item>
///   <item><c>DELETE</c> → <see cref="TerminateAsync"/> — flips the current
///         head to <see cref="OrganizationVersionState.Terminated"/>
///         (soft-delete via versioning).</item>
/// </list>
/// </remarks>
public interface IOrganizationService
{
    Task<Organization?> GetByIdAsync(string organizationId);
    Task<Organization?> GetVersionAsync(string organizationId, string versionId);

    Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType, LineOfBusiness? lineOfBusiness, string? parentOrganizationId, int page, int pageSize);

    Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId);

    Task<Organization> CreateAndActivateAsync(Organization candidate, string actorId);

    Task<Organization> UpdateAsync(string organizationId, Organization candidate, string actorId);

    Task<Organization> TerminateAsync(string organizationId, string reason, string actorId);
}

public class OrganizationService : IOrganizationService
{
    private static readonly JsonSerializerOptions _cloneOpts = new(JsonSerializerDefaults.Web);

    private readonly IOrganizationRepository _repository;
    private readonly ILogger<OrganizationService> _logger;

    public OrganizationService(
        IOrganizationRepository repository,
        ILogger<OrganizationService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task<Organization?> GetByIdAsync(string organizationId)
        => _repository.GetByIdAsync(organizationId);

    public Task<Organization?> GetVersionAsync(string organizationId, string versionId)
        => _repository.GetVersionAsync(organizationId, versionId);

    public Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType, LineOfBusiness? lineOfBusiness, string? parentOrganizationId, int page, int pageSize)
        => _repository.ListAsync(networkType, lineOfBusiness, parentOrganizationId, page, pageSize);

    public Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId)
        => _repository.GetByParentAsync(parentOrganizationId);

    public async Task<Organization> CreateAndActivateAsync(Organization candidate, string actorId)
    {
        if (string.IsNullOrEmpty(candidate.Id)) candidate.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(candidate.OrganizationId)) candidate.OrganizationId = candidate.Id;

        candidate.VersionId = OrganizationVersionId.NewId();
        candidate.VersionNumber = 1;
        candidate.VersionState = OrganizationVersionState.Draft;
        candidate.PredecessorVersionId = null;
        candidate.ActivatedAt = null;
        candidate.ActivatedBy = null;
        candidate.SuspendedAt = null;
        candidate.SuspensionReason = null;
        candidate.SupersededAt = null;
        candidate.SupersededByVersionId = null;
        candidate.CreatedBy = string.IsNullOrEmpty(candidate.CreatedBy) ? actorId : candidate.CreatedBy;
        candidate.CreatedDate = DateTime.UtcNow;
        candidate.LastUpdatedDate = DateTime.UtcNow;
        candidate.LastUpdatedBy = actorId;

        var draft = await _repository.CreateDraftAsync(candidate);

        var now = DateTime.UtcNow;
        draft.VersionState = OrganizationVersionState.Active;
        draft.ActivatedAt = now;
        draft.ActivatedBy = actorId;
        draft.Status = OrganizationStatus.Active;
        draft.LastUpdatedBy = actorId;

        return await _repository.ActivateAndSupersedeAsync(draft, predecessor: null);
    }

    public async Task<Organization> UpdateAsync(string organizationId, Organization candidate, string actorId)
    {
        var current = await _repository.GetByIdAsync(organizationId)
            ?? throw new OrganizationVersionStateException(organizationId, string.Empty, OrganizationVersionState.Active,
                $"Organization {organizationId} not found") { IsNotFound = true };

        if (current.VersionState == OrganizationVersionState.Terminated)
        {
            throw new OrganizationVersionStateException(
                current.OrganizationId, current.VersionId, current.VersionState,
                $"Organization {current.OrganizationId} is Terminated and cannot be updated. Reactivate first.");
        }

        // Build a brand-new draft cloned from the candidate fields, but
        // wire identity (chain key, predecessor, version number) from the
        // current head so the versioning chain stays intact. This is a
        // RESTful PUT — full replacement: any field absent from the
        // candidate is treated as "set to default" on the new version.
        // See NetworksController.Update XML doc + network-as-organization.md.
        var draft = Clone(candidate);
        draft.Id = Guid.NewGuid().ToString();
        draft.TenantId = current.TenantId;
        draft.OrganizationId = current.OrganizationId;
        draft.VersionId = OrganizationVersionId.NewId();
        draft.VersionNumber = current.VersionNumber + 1;
        draft.VersionState = OrganizationVersionState.Draft;
        draft.PredecessorVersionId = current.VersionId;
        draft.ActivatedAt = null;
        draft.ActivatedBy = null;
        draft.SuspendedAt = null;
        draft.SuspensionReason = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        draft.CreatedBy = current.CreatedBy ?? actorId;
        draft.CreatedDate = current.CreatedDate;
        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.LastUpdatedBy = actorId;

        var stored = await _repository.CreateDraftAsync(draft);

        var now = DateTime.UtcNow;
        stored.VersionState = OrganizationVersionState.Active;
        stored.ActivatedAt = now;
        stored.ActivatedBy = actorId;
        stored.Status = OrganizationStatus.Active;
        stored.LastUpdatedBy = actorId;

        // Carry the prior head into Superseded atomically.
        current.VersionState = OrganizationVersionState.Superseded;
        current.SupersededAt = now;
        current.SupersededByVersionId = stored.VersionId;
        current.Status = OrganizationStatus.Inactive;

        return await _repository.ActivateAndSupersedeAsync(stored, current);
    }

    public async Task<Organization> TerminateAsync(string organizationId, string reason, string actorId)
    {
        var current = await _repository.GetByIdAsync(organizationId)
            ?? throw new OrganizationVersionStateException(organizationId, string.Empty, OrganizationVersionState.Active,
                $"Organization {organizationId} not found") { IsNotFound = true };

        if (current.VersionState == OrganizationVersionState.Terminated)
        {
            // Already terminated — return the existing row instead of churning a no-op write.
            return current;
        }

        var now = DateTime.UtcNow;
        current.VersionState = OrganizationVersionState.Terminated;
        current.TerminationDate = now;
        current.Status = OrganizationStatus.Terminated;
        current.LastUpdatedBy = actorId;
        current.TerminationReason = string.IsNullOrEmpty(reason) ? current.TerminationReason : reason;

        return await _repository.ReplaceVersionRowAsync(current);
    }

    private static Organization Clone(Organization src)
        => JsonSerializer.Deserialize<Organization>(JsonSerializer.Serialize(src, _cloneOpts), _cloneOpts)!;
}
