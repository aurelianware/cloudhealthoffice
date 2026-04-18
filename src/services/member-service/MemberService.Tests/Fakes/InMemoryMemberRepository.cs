using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>In-memory <see cref="IMemberRepository"/> for controller and integration tests.</summary>
public sealed class InMemoryMemberRepository : IMemberRepository
{
    public List<Member> Members { get; } = new();

    public Task<Member?> GetByIdAsync(string tenantId, string id)
        => Task.FromResult(Members.FirstOrDefault(m => m.TenantId == tenantId && m.Id == id));

    public Task<Member?> GetByMemberIdAsync(string tenantId, string memberId)
        => Task.FromResult(Members.FirstOrDefault(m => m.TenantId == tenantId && m.MemberId == memberId));

    public Task<(IEnumerable<Member> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? groupNumber = null,
        string? lastName = null,
        DateTime? dateOfBirth = null,
        bool activeOnly = false,
        bool subscribersOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        IEnumerable<Member> q = Members.Where(m => m.TenantId == tenantId);
        if (!string.IsNullOrEmpty(groupNumber)) q = q.Where(m => m.GroupNumber == groupNumber);
        if (!string.IsNullOrEmpty(lastName)) q = q.Where(m => m.LastName.Contains(lastName, StringComparison.OrdinalIgnoreCase));
        if (dateOfBirth.HasValue) q = q.Where(m => m.DateOfBirth.Date == dateOfBirth.Value.Date);
        if (activeOnly) q = q.Where(m => m.Status == EnrollmentStatus.Active);
        if (subscribersOnly) q = q.Where(m => m.IsSubscriber);
        return Task.FromResult<(IEnumerable<Member>, string?)>((q.Take(pageSize).ToList(), null));
    }

    public Task<List<Member>> GetDependentsAsync(string tenantId, string subscriberMemberId)
#pragma warning disable CS0618
        => Task.FromResult(Members
            .Where(m => m.TenantId == tenantId && m.SubscriberMemberId == subscriberMemberId && !m.IsSubscriber)
            .ToList());
#pragma warning restore CS0618

    public Task<int> GetCountByGroupAsync(string tenantId, string groupNumber)
        => Task.FromResult(Members.Count(m => m.TenantId == tenantId && m.GroupNumber == groupNumber));

    public Task<Member> CreateAsync(Member member)
    {
        Members.Add(member);
        return Task.FromResult(member);
    }

    public Task<Member> UpdateAsync(Member member)
    {
        var idx = Members.FindIndex(m => m.TenantId == member.TenantId && m.Id == member.Id);
        if (idx >= 0) Members[idx] = member;
        else Members.Add(member);
        return Task.FromResult(member);
    }

    public Task DeleteAsync(string tenantId, string id)
    {
        Members.RemoveAll(m => m.TenantId == tenantId && m.Id == id);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string tenantId, string memberId)
        => Task.FromResult(Members.Any(m => m.TenantId == tenantId && m.MemberId == memberId));

    public Task<Member?> GetByIdentifierAsync(string tenantId, string system, string value)
        => Task.FromResult(Members.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.Identifiers.Any(i => i.System == system && i.Value == value)));
}
