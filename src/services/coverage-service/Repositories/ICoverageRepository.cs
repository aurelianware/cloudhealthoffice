using CoverageService.Models;

namespace CoverageService.Repositories;

/// <summary>
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
    /// <summary>
    /// Coverages in any tenant whose status their date span has moved on as of
    /// <paramref name="today"/> (see <see cref="Coverage.DueStatusTransition"/>),
    /// at most <paramref name="maxItems"/>. Read by the daily status sweep.
    /// </summary>
    Task<List<Coverage>> GetStatusTransitionsDueAsync(DateTime today, int maxItems);
    /// <summary>
    /// Sets the status (and audit fields) of <paramref name="observed"/> only,
    /// and only while the stored status, effective date and termination date
    /// are still the ones in <paramref name="observed"/> (as the sweep read
    /// it). False when any changed concurrently or the coverage is gone.
    /// </summary>
    Task<bool> SetStatusAsync(Coverage observed, CoverageStatus newStatus, string updatedBy);
    Task<Coverage> CreateAsync(Coverage coverage);
    Task<Coverage> UpdateAsync(Coverage coverage);
    Task DeleteAsync(string tenantId, string id);
}
