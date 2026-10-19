using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Storage seam for the <see cref="Organization"/> network entity. Each
/// row is one immutable version; default reads resolve to the latest
/// non-Draft head, mirroring <see cref="IProviderRepository"/>.
/// </summary>
public interface IOrganizationRepository
{
    /// <summary>Latest non-Draft version of <paramref name="organizationId"/>.</summary>
    Task<Organization?> GetByIdAsync(string organizationId);

    /// <summary>
    /// Look up a single version by <c>VersionId</c>. Returns null when
    /// either the chain key or the version id does not resolve.
    /// </summary>
    Task<Organization?> GetVersionAsync(string organizationId, string versionId);

    /// <summary>
    /// Latest <see cref="OrganizationVersionState.Active"/> version effective
    /// at <paramref name="asOf"/>. Returns null when no Active version exists.
    /// </summary>
    Task<Organization?> GetLatestActiveAsync(string organizationId, DateTime asOf);

    /// <summary>Newest-first list of every version for a chain.</summary>
    Task<(IReadOnlyList<Organization> Items, string? ContinuationToken)> ListVersionsAsync(
        string organizationId, int pageSize, string? continuationToken);

    /// <summary>
    /// List networks for the current tenant, optionally filtered by
    /// <see cref="NetworkType"/> and <see cref="LineOfBusiness"/>. Returns
    /// the head (latest non-Draft) of every chain that matches.
    /// </summary>
    Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType,
        LineOfBusiness? lineOfBusiness,
        string? parentOrganizationId,
        int page,
        int pageSize);

    /// <summary>Children of <paramref name="parentOrganizationId"/> for partOf traversal.</summary>
    Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId);

    /// <summary>Persist a Draft. Caller sets identity + state.</summary>
    Task<Organization> CreateDraftAsync(Organization draft);

    /// <summary>Update a Draft. Throws when the row is not Draft.</summary>
    Task<Organization> UpdateDraftAsync(Organization draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToActivate"/> to Active
    /// and (if not null) <paramref name="predecessor"/> to Superseded.
    /// </summary>
    Task<Organization> ActivateAndSupersedeAsync(Organization draftToActivate, Organization? predecessor);

    /// <summary>Persist a state-only mutation on an existing version row.</summary>
    Task<Organization> ReplaceVersionRowAsync(Organization version);
}

/// <summary>
/// Thrown when a write violates the version-state invariants on the
/// network chain. Mirrors <see cref="ProviderVersionStateException"/>.
/// </summary>
public sealed class OrganizationVersionStateException : InvalidOperationException
{
    public string OrganizationId { get; }
    public string VersionId { get; }
    public OrganizationVersionState CurrentState { get; }

    public bool IsNotFound { get; init; }

    public OrganizationVersionStateException(
        string organizationId, string versionId, OrganizationVersionState currentState, string message)
        : base(message)
    {
        OrganizationId = organizationId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}
