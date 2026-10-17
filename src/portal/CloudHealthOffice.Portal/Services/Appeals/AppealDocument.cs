namespace CloudHealthOffice.Portal.Services;

public class AppealDocument
{
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public DateTime UploadedDate { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
}
