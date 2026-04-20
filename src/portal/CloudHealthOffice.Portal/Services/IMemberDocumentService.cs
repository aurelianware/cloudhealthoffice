using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

public interface IMemberDocumentService
{
    Task<List<MemberDocumentSummary>> GetDocumentsAsync(string memberId, string? category = null);
    Task<MemberDocumentSummary?> GetDocumentAsync(string documentId);
    Task ToggleLegalHoldAsync(string documentId, bool legalHold);
    Task<Stream> DownloadDocumentAsync(string documentId);
    Task<string> UploadDocumentAsync(MemberDocumentUploadRequest request, Stream fileStream);
    Task<JsonDocument?> GetFhirDocumentReferencesAsync(string memberId, string? category = null);
}

public class MemberDocumentSummary
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Subcategory { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public string RetentionPolicyId { get; set; } = string.Empty;
    public DateTime RetentionUntilDate { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedDate { get; set; }
    public bool LegalHold { get; set; }
    public string? StateCode { get; set; }
}

public class MemberDocumentUploadRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Subcategory { get; set; }
    public string Source { get; set; } = "Uploaded";
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public string? RetentionPolicyId { get; set; }
    public string? UploadedBy { get; set; }
    public bool LegalHold { get; set; }
    public string? StateCode { get; set; }
    public DateTime? CoverageTerminationDate { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
}
