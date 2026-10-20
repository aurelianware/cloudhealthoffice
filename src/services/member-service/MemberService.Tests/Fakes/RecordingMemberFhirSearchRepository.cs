using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>
/// HTTP-pipeline stand-in for <see cref="IMemberFhirSearchRepository"/>: returns
/// the members it is given for the tenant asked, and records each call so a test
/// can assert the tenant and filters the endpoint passed. Query semantics are
/// covered against real MongoDB in <c>MemberFhirSearchRepositoryTests</c>.
/// </summary>
public sealed class RecordingMemberFhirSearchRepository : IMemberFhirSearchRepository
{
    public List<Member> Members { get; } = new();

    public List<(string TenantId, MemberFhirSearchQuery Query)> Calls { get; } = new();

    public Task<(IReadOnlyList<Member> Items, long Total)> SearchAsync(
        string tenantId, MemberFhirSearchQuery query, CancellationToken ct = default)
    {
        Calls.Add((tenantId, query));
        var matches = Members.Where(m => m.TenantId == tenantId && !m.IsDraft).OrderBy(m => m.MemberId).ToList();
        IReadOnlyList<Member> page = matches.Skip(query.Offset).Take(query.Limit).ToList();
        return Task.FromResult((page, (long)matches.Count));
    }

    public Task<IReadOnlyDictionary<string, string?>> SubscribersAsync(
        string tenantId, IReadOnlyList<Member> members, CancellationToken ct = default)
    {
#pragma warning disable CS0618
        IReadOnlyDictionary<string, string?> map = members.ToDictionary(
            m => m.MemberId, m => m.IsSubscriber ? null : m.SubscriberMemberId);
#pragma warning restore CS0618
        return Task.FromResult(map);
    }
}
