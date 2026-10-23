using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// What a subject's documents are asked for. The catalog answers "documents
/// for claim X", never "document Y", so every call names a subject
/// (docs/architecture/document-store.md §5).
/// </summary>
public enum DocumentSubjectKind
{
    Member,
    Claim,
    ClaimLine,
    Authorization,
    Appeal,
    Grievance,
    Call,
    Provider,
    RfaiRequest,
    PaymentRun,
}

/// <summary>One link the caller is allowed to see, as the catalog returns it.</summary>
public sealed class DocumentLinkSummary
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentSubjectKind Kind { get; init; }

    public string Id { get; init; } = string.Empty;
}

public sealed class DocumentSourceSummary
{
    public string? Channel { get; init; }
    public string? ExternalId { get; init; }
}

/// <summary>Whether the file has been scanned, and what the scan found.</summary>
public enum DocumentScanStatus
{
    Pending,
    Clean,
    Infected,
    Failed,
}

/// <summary>A catalog document as the panel shows it. No storage location ever reaches here.</summary>
public sealed class CatalogDocumentSummary
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int DocumentVersion { get; init; }
    public DocumentSourceSummary Source { get; init; } = new();
    public IReadOnlyList<DocumentLinkSummary> Links { get; init; } = [];

    /// <summary>Subjects this document also supports that the caller may not read: counted, not named.</summary>
    public int OtherLinks { get; init; }

    public IReadOnlyList<string> Sensitivity { get; init; } = [];
    public bool LegalHold { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentScanStatus ScanStatus { get; init; }

    public bool UploadPending { get; init; }
    public DateTime ReceivedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>Openable only once the scan is clean and the upload has finished.</summary>
    public bool CanOpen => !UploadPending && ScanStatus == DocumentScanStatus.Clean;
}

/// <summary>
/// The documents on one subject, and how many were withheld. A withheld count
/// is shown rather than swallowed, so nobody reads a filtered list as
/// "nothing on file".
/// </summary>
public sealed class DocumentSubjectListing
{
    public IReadOnlyList<CatalogDocumentSummary> Documents { get; init; } = [];
    public int Withheld { get; init; }

    /// <summary>The caller lacks the subject's read permission; the panel says so rather than showing an empty list.</summary>
    public bool Forbidden { get; init; }

    public static readonly DocumentSubjectListing Empty = new();
    public static readonly DocumentSubjectListing Denied = new() { Forbidden = true };
}

public interface IDocumentCatalogService
{
    /// <summary>The documents linked to one subject that the signed-in user may see.</summary>
    Task<DocumentSubjectListing> GetForSubjectAsync(
        DocumentSubjectKind kind, string subjectId, CancellationToken ct = default);

    /// <summary>The document's content, opened from the subject it is shown under.</summary>
    Task<HttpResponseMessage> DownloadAsync(
        string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default);
}

/// <summary>
/// Reads the document catalog (document-service) for the signed-in user: the
/// scoped <see cref="HttpClient"/> carries their CHO token, so the catalog
/// applies their permissions and writes their name to the access audit. The
/// portal adds no access rule of its own and never sees a storage path.
/// </summary>
public sealed class DocumentCatalogService : IDocumentCatalogService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentCatalogService> _logger;

    public DocumentCatalogService(HttpClient httpClient, IConfiguration configuration, ILogger<DocumentCatalogService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DocumentSubjectListing> GetForSubjectAsync(
        DocumentSubjectKind kind, string subjectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) return DocumentSubjectListing.Empty;

        try
        {
            using var response = await _httpClient.GetAsync(ListUrl(kind, subjectId), ct);

            // A subject the user may not read is a real answer, not a failure:
            // the panel says "you cannot see these" rather than "none".
            if (response.StatusCode is HttpStatusCode.Forbidden)
                return DocumentSubjectListing.Denied;
            if (response.StatusCode is HttpStatusCode.NotFound)
                return DocumentSubjectListing.Empty;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<DocumentSubjectListing>(ct)
                   ?? DocumentSubjectListing.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Document Service");
            throw new ServiceUnavailableException("Document Service", ex);
        }
    }

    public async Task<HttpResponseMessage> DownloadAsync(
        string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}/api/v1/documents/{Uri.EscapeDataString(documentId)}/content" +
                  $"?subjectKind={kind}&subjectId={Uri.EscapeDataString(subjectId)}";
        try
        {
            return await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Document Service");
            throw new ServiceUnavailableException("Document Service", ex);
        }
    }

    private string ListUrl(DocumentSubjectKind kind, string subjectId) =>
        $"{BaseUrl()}/api/v1/documents?subjectKind={kind}&subjectId={Uri.EscapeDataString(subjectId)}";

    private string BaseUrl()
    {
        var configured = _configuration["Services:DocumentService"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Services:DocumentService is not configured.");
        return configured.TrimEnd('/');
    }
}
