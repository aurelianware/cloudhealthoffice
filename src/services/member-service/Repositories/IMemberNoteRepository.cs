using MemberService.Models;

namespace MemberService.Repositories;

public interface IMemberNoteRepository
{
    Task<MemberNote> CreateAsync(MemberNote note);
    Task<MemberNote?> GetByIdAsync(string tenantId, string memberId, string noteId);

    /// <summary>
    /// Page notes for a member, newest first. <paramref name="category"/> optional;
    /// when null, returns all categories.
    /// </summary>
    Task<(IReadOnlyList<MemberNote> Items, string? ContinuationToken)> ListByMemberAsync(
        string tenantId,
        string memberId,
        MemberNoteCategory? category,
        int pageSize,
        string? continuationToken);
}
