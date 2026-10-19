namespace CloudHealthOffice.Portal.Services;

public interface IAttachmentService
{
    Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId);
    Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType);
    Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId);
    Task DeleteAttachmentAsync(string authorizationId, string attachmentId);
}
