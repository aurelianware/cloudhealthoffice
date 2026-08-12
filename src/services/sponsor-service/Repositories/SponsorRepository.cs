using SponsorService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    Task DeleteAsync(string tenantId, string id);
    Task<bool> ExistsAsync(string tenantId, string groupNumber);
    Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null);
}
