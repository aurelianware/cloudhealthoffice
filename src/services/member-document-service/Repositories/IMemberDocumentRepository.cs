using MemberDocumentService.Models;

namespace MemberDocumentService.Repositories;

public interface IMemberDocumentRepository
{
    Task<MemberDocument> CreateAsync(MemberDocument document);
    Task<MemberDocument?> GetByIdAsync(string tenantId, string id);
    Task<IReadOnlyList<MemberDocument>> ListByMemberIdAsync(string tenantId, string memberId, string? category = null);
    Task<MemberDocument> UpdateAsync(MemberDocument document);

    /// <summary>Removes a record whose file never reached the catalog (no DocumentId). A filed record is never deleted here.</summary>
    Task DeleteUnfiledAsync(string tenantId, string id);
}
