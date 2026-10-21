using AttachmentService.Models;

namespace AttachmentService.Repositories;

public interface IAttachmentRepository
{
    Task<Attachment> CreateAsync(Attachment attachment);
    Task<Attachment?> GetByIdAsync(string id, string tenantId);
    Task<IEnumerable<Attachment>> GetByClaimIdAsync(string claimId, string tenantId);
    Task<IEnumerable<Attachment>> GetByAuthorizationIdAsync(string authorizationId, string tenantId);
    Task<IEnumerable<Attachment>> GetByAppealIdAsync(string appealId, string tenantId);
    Task<Attachment?> GetByRFAIReferenceAsync(string rfaiReference, string tenantId);
    Task<Attachment> UpdateAsync(Attachment attachment);

    /// <summary>The tenant's attachments held as Unmatched, oldest first.</summary>
    Task<IReadOnlyList<Attachment>> GetUnmatchedAsync(string tenantId, int limit = 200);

    /// <summary>Unmatched attachments in every tenant, oldest first: the background matcher's work list.</summary>
    Task<IReadOnlyList<Attachment>> ListUnmatchedAsync(int limit);
    Task DeleteAsync(string id, string tenantId);
}
