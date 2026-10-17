using SponsorService.Models;

namespace SponsorService.Repositories;

/// <summary>
/// Repository interface for Sponsor entities
/// </summary>
public interface ISponsorRepository
{
    Task<Sponsor?> GetByIdAsync(string tenantId, string id);
    Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber);
    Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId,
        SponsorStatus? status = null,
        bool activeOnly = false,
        LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20,
        string? continuationToken = null);
    Task<Sponsor> CreateAsync(Sponsor sponsor);
    Task<Sponsor> UpdateAsync(Sponsor sponsor);

    /// <summary>
    /// Applies a status change and nothing else: Status, StatusReason,
    /// StatusChangedBy, StatusChangedDate, LastUpdatedBy, LastUpdatedDate and an
    /// appended StatusHistory entry. Applied only while the stored status still
    /// equals <see cref="SponsorStatusChange.From"/>; returns false (nothing
    /// written) when the sponsor is gone or its status changed meanwhile.
    /// </summary>
    Task<bool> UpdateStatusAsync(string tenantId, string id, SponsorStatusChange change);

    Task DeleteAsync(string tenantId, string id);
    Task<bool> ExistsAsync(string tenantId, string groupNumber);
    Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null);
}
