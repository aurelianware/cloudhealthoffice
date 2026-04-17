using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>
/// Wraps <see cref="InMemoryMemberEventRepository"/> and simulates a Cosmos
/// <c>/version</c> unique-key violation for the first N append attempts. Lets
/// us exercise <c>CosmosMemberEventPublisher</c>'s retry loop.
/// </summary>
public sealed class ConflictingMemberEventRepository : IMemberEventRepository
{
    private readonly InMemoryMemberEventRepository _inner;
    private int _remainingConflicts;

    public ConflictingMemberEventRepository(InMemoryMemberEventRepository inner, int conflicts)
    {
        _inner = inner;
        _remainingConflicts = conflicts;
    }

    public int AttemptsSeen { get; private set; }

    public async Task<AppendResult> AppendAsync(MemberEvent evt, CancellationToken ct = default)
    {
        AttemptsSeen++;
        if (_remainingConflicts > 0)
        {
            _remainingConflicts--;
            // Emulate Cosmos unique-key-policy violation on /version: Appended=false
            // with no existing event — forces the publisher to bump Version and retry.
            return new AppendResult(evt, Appended: false);
        }
        return await _inner.AppendAsync(evt, ct);
    }

    public Task<IReadOnlyList<MemberEvent>> ListByMemberAsync(string tenantId, string memberId, CancellationToken ct = default)
        => _inner.ListByMemberAsync(tenantId, memberId, ct);

    public Task<MemberEvent?> GetByIdAsync(string tenantId, string memberId, string eventId, CancellationToken ct = default)
        => _inner.GetByIdAsync(tenantId, memberId, eventId, ct);

    public Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default)
        => _inner.GetNextVersionAsync(tenantId, memberId, ct);
}
