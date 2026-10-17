using MemberService.Models;

namespace MemberService.Repositories;

/// <summary>
/// Repository interface for Member entities
/// </summary>
public interface IMemberRepository
{
    Task<Member?> GetByIdAsync(string tenantId, string id);
    Task<Member?> GetByMemberIdAsync(string tenantId, string memberId);
    Task<(IEnumerable<Member> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? groupNumber = null,
        string? lastName = null,
        DateTime? dateOfBirth = null,
        bool activeOnly = false,
        bool subscribersOnly = false,
        int pageSize = 20,
        string? continuationToken = null);
    Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId);
    Task<int> GetCountByGroupAsync(string tenantId, string groupNumber);
    Task<Member> CreateAsync(Member member);
    Task<Member> UpdateAsync(Member member);
    Task DeleteAsync(string tenantId, string id);
    Task<bool> ExistsAsync(string tenantId, string memberId);

    /// <summary>
    /// Find a member by typed identifier (system + value). Used for idempotent member
    /// creation and portal lookups.
    /// </summary>
    Task<Member?> GetByIdentifierAsync(string tenantId, string system, string value);
}
