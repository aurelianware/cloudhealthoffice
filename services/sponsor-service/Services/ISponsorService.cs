using SponsorService.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SponsorService.Services;

/// <summary>
/// Sponsor business logic service interface
/// </summary>
public interface ISponsorService
{
    Task<(IEnumerable<Sponsor> Sponsors, string? ContinuationToken, int TotalCount)> GetSponsorsAsync(
        string tenantId,
        SponsorStatus? status = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null);

    Task<Sponsor?> GetSponsorByGroupNumberAsync(string tenantId, string groupNumber);
    
    Task<Sponsor> CreateSponsorAsync(string tenantId, Sponsor sponsor, string createdBy);
    
    Task<Sponsor> UpdateSponsorAsync(string tenantId, Sponsor sponsor, string updatedBy);
    
    Task TerminateSponsorAsync(string tenantId, string groupNumber, DateTime terminationDate, string updatedBy);
    
    Task<bool> ExistsByGroupNumberAsync(string tenantId, string groupNumber);
    
    Task UpdateMemberCountsAsync(string tenantId, string groupNumber, int totalMembers, int totalDependents);
}
