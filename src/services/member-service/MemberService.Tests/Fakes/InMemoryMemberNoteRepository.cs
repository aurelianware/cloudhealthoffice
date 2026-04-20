using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>In-memory <see cref="IMemberNoteRepository"/> for tests.</summary>
public sealed class InMemoryMemberNoteRepository : IMemberNoteRepository
{
    public List<MemberNote> Notes { get; } = new();
    private readonly object _lock = new();

    public Task<MemberNote> CreateAsync(MemberNote note)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString();
            if (note.CreatedDate == default) note.CreatedDate = DateTime.UtcNow;
            Notes.Add(note);
            return Task.FromResult(note);
        }
    }

    public Task<MemberNote?> GetByIdAsync(string tenantId, string memberId, string noteId)
    {
        lock (_lock)
        {
            var hit = Notes.FirstOrDefault(n =>
                n.TenantId == tenantId && n.MemberId == memberId && n.Id == noteId);
            return Task.FromResult<MemberNote?>(hit);
        }
    }

    public Task<(IReadOnlyList<MemberNote> Items, string? ContinuationToken)> ListByMemberAsync(
        string tenantId,
        string memberId,
        MemberNoteCategory? category,
        int pageSize,
        string? continuationToken)
    {
        lock (_lock)
        {
            int skip = 0;
            if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
                skip = parsed;

            var q = Notes
                .Where(n => n.TenantId == tenantId && n.MemberId == memberId);
            if (category.HasValue) q = q.Where(n => n.Category == category.Value);

            var ordered = q.OrderByDescending(n => n.CreatedDate).ToList();
            var page = ordered.Skip(skip).Take(pageSize).ToList();
            string? next = (skip + page.Count) < ordered.Count
                ? (skip + page.Count).ToString()
                : null;
            return Task.FromResult<(IReadOnlyList<MemberNote>, string?)>((page, next));
        }
    }
}
