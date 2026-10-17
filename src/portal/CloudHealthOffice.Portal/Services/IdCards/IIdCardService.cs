namespace CloudHealthOffice.Portal.Services;

public interface IIdCardService
{
    Task<IdCardOrderView> OrderAsync(string memberId, string? languageCode = null, string? requestedBy = null);
    Task<IdCardOrderView?> GetOrderAsync(string orderId);
    Task<List<IdCardHistoryView>> ListForMemberAsync(string memberId);
    /// <summary>The portal-relative download link for a document (never a backend URL).</summary>
    string BuildDocumentDownloadUrl(string documentId);
    Task RevokeAsync(string cardId, string reason, string? notes = null);
}
