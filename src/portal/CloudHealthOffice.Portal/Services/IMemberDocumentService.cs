using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

public interface IMemberDocumentService
{
    Task<List<MemberDocumentSummary>> GetDocumentsAsync(string memberId, string? category = null);
    Task<MemberDocumentSummary?> GetDocumentAsync(string documentId);
    Task ToggleLegalHoldAsync(string documentId, bool legalHold);
    Task<Stream> DownloadDocumentAsync(string documentId);

    /// <summary>
    /// Opens a document's content at member-document-service with the signed-in
    /// user's CHO token, without buffering it. The caller disposes the result.
    /// A refusal (401/403/404/409) is returned as its status, not thrown.
    /// </summary>
    Task<MemberDocumentContent> OpenDocumentContentAsync(string documentId, CancellationToken cancellationToken = default);
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
    public bool LegalHold { get; set; }
    public string? StateCode { get; set; }
    public DateTime? CoverageTerminationDate { get; set; }
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The file's real content type (one of <see cref="MemberDocumentContentTypes.Allowed"/>).
    /// Leave empty to derive it from <see cref="FileName"/>'s extension. Anything
    /// else is refused before upload. The uploader is always the token subject,
    /// so the request carries none.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>An open document download from member-document-service.</summary>
public sealed class MemberDocumentContent : IDisposable
{
    private readonly IDisposable? _owner;

    public MemberDocumentContent(System.Net.HttpStatusCode statusCode, Stream? content = null,
        string? contentType = null, string? fileName = null, IDisposable? owner = null)
    {
        StatusCode = statusCode;
        Content = content;
        ContentType = contentType;
        FileName = fileName;
        _owner = owner;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
    public bool Succeeded => (int)StatusCode is >= 200 and < 300 && Content != null;
    public Stream? Content { get; }
    public string? ContentType { get; }
    public string? FileName { get; }

    public void Dispose()
    {
        Content?.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>Thrown before upload when a file's type is not one member-document-service accepts.</summary>
public sealed class UnsupportedDocumentTypeException : Exception
{
    public UnsupportedDocumentTypeException(string? fileName)
        : base(MemberDocumentContentTypes.RefusalMessage)
    {
        FileName = fileName;
    }

    public string? FileName { get; }
}

/// <summary>
/// The document types member-document-service accepts (its default
/// <c>MemberDocuments:AllowedContentTypes</c>: PDF, PNG, JPEG, TIFF, each checked
/// against the file's first bytes there). The portal refuses anything else before
/// sending, with <see cref="RefusalMessage"/>, rather than letting the service answer 415.
/// </summary>
public static class MemberDocumentContentTypes
{
    public const string RefusalMessage =
        "This file type can't be uploaded. Member documents must be PDF, PNG, JPEG or TIFF files.";

    /// <summary>For an upload control's <c>accept</c> attribute.</summary>
    public const string AcceptAttribute =
        ".pdf,.png,.jpg,.jpeg,.tif,.tiff,application/pdf,image/png,image/jpeg,image/tiff";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/png", "image/jpeg", "image/tiff",
    };

    private static readonly IReadOnlyDictionary<string, string> ByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
        };

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpg"] = "image/jpeg",
            ["image/pjpeg"] = "image/jpeg",
            ["image/tif"] = "image/tiff",
            ["image/x-tiff"] = "image/tiff",
            ["image/x-png"] = "image/png",
        };

    /// <summary>
    /// The content type to upload <paramref name="fileName"/> as: the browser's
    /// type when it is an allowed one; otherwise, when the browser gave none or a
    /// generic <c>application/octet-stream</c>, the type for the file's extension.
    /// Null when the file is not an allowed type (including a browser type such as
    /// <c>text/html</c> on a file named <c>.pdf</c>).
    /// </summary>
    public static string? Resolve(string? fileName, string? browserContentType)
    {
        var declared = Normalize(browserContentType);
        if (declared != null && Allowed.Contains(declared))
            return declared;

        if (declared != null && declared != "application/octet-stream")
            return null;

        var extension = Path.GetExtension(fileName ?? string.Empty);
        return !string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var byExtension)
            ? byExtension
            : null;
    }

    private static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (mediaType.Length == 0)
            return null;
        return Aliases.TryGetValue(mediaType, out var canonical) ? canonical : mediaType;
    }
}
