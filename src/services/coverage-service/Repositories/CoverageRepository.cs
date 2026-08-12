using CoverageService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// Repository interface for Coverage entities
/// </summary>
public interface ICoverageRepository
{
    Task<Coverage?> GetByIdAsync(string tenantId, string id);
    Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(string tenantId, string memberId, DateTime serviceDate, string? insuranceLineCode = null);
    Task<List<Coverage>> GetCoverageHistoryAsync(string tenantId, string memberId, bool includeTerminated = true);
    Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null);
    Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber);
    Task<List<Coverage>> GetByPcpNpiAsync(string tenantId, string pcpNpi, CoverageStatus? status = null, LineOfBusiness? lineOfBusiness = null);
    Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null);
    Task<Coverage> CreateAsync(Coverage coverage);
    Task<Coverage> UpdateAsync(Coverage coverage);
    Task DeleteAsync(string tenantId, string id);
}
