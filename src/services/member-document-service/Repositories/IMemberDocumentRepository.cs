using MemberDocumentService.Models;

namespace MemberDocumentService.Repositories;

public interface IMemberDocumentRepository
{
    Task<MemberDocument> CreateAsync(MemberDocument document);
    Task<MemberDocument?> GetByIdAsync(string tenantId, string id);
    Task<IReadOnlyList<MemberDocument>> ListByMemberIdAsync(string tenantId, string memberId, string? category = null);
    Task<MemberDocument> UpdateAsync(MemberDocument document);
}
