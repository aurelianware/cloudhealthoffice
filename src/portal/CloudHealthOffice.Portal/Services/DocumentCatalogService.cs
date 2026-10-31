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

    /// <summary>A file-drop run (claims-service); its documents are the files it loaded, as received.</summary>
    ImportRun,
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

/// <summary>
/// A stored C-CDA read into what a UM reviewer needs: the sections the sender
/// wrote, and the entries CHO understood from them.
///
/// Derived by document-service on every request and stored nowhere
/// (docs/architecture/ccda.md). Internal to CHO's own reviewers; nothing here
/// is served on a member-facing or provider-facing API.
/// </summary>
public sealed class ClinicalSummaryView
{
    public string? Title { get; init; }

    /// <summary>The sender's effective time, verbatim in HL7's form.</summary>
    public string? EffectiveTime { get; init; }

    public IReadOnlyList<ClinicalSummarySectionView> Sections { get; init; } = [];

    /// <summary>
    /// Whether the document is filed against a member. False means the entries
    /// were read but attributed to nobody, which is why a section can show a
    /// count and no lines.
    /// </summary>
    public bool MemberLinked { get; init; }

    /// <summary>Why CHO could not read it, where it could not.</summary>
    public string? Refusal { get; init; }

    /// <summary>The caller may not open this document; the panel says so rather than showing nothing.</summary>
    public bool Forbidden { get; init; }

    public static readonly ClinicalSummaryView Denied = new() { Forbidden = true };

    public static ClinicalSummaryView Unreadable(string reason) => new() { Refusal = reason };
}

/// <summary>One section of the document. An unmapped section still appears, named and counted.</summary>
public sealed class ClinicalSummarySectionView
{
    public string Kind { get; init; } = string.Empty;
    public string? Title { get; init; }

    /// <summary>Whether CHO turned this section's entries into clinical facts.</summary>
    public bool Mapped { get; init; }

    /// <summary>How many coded entries the sender wrote, mapped or not.</summary>
    public int EntryCount { get; init; }

    public IReadOnlyList<ClinicalSummaryItemView> Items { get; init; } = [];

    /// <summary>The sender's own narrative, which is all there is for an unmapped section.</summary>
    public string? NarrativeXml { get; init; }
}

/// <summary>One entry CHO understood.</summary>
public sealed class ClinicalSummaryItemView
{
    public string Display { get; init; } = string.Empty;
    public string? Status { get; init; }
    public string? Detail { get; init; }
}

public sealed record DocumentScanRetryResult(bool Confirmed, DocumentScanStatus? Status, string Message);

public interface IDocumentCatalogService
{
    Task<string?> UploadAsync(DocumentSubjectKind kind, string subjectId, string title, string type,
        Stream content, string fileName, string contentType, CancellationToken ct = default);

    Task<DocumentScanRetryResult> RetryScanAsync(string documentId, DocumentSubjectKind kind, string subjectId,
        CancellationToken ct = default);

    /// <summary>Add an additional subject link, using an existing authorized subject context.</summary>
    Task<string?> AddLinkAsync(string documentId, DocumentSubjectKind sourceKind, string sourceId,
        DocumentSubjectKind targetKind, string targetId, CancellationToken ct = default);

    /// <summary>The documents linked to one subject that the signed-in user may see.</summary>
    Task<DocumentSubjectListing> GetForSubjectAsync(
        DocumentSubjectKind kind, string subjectId, CancellationToken ct = default);

    /// <summary>The document's content, opened from the subject it is shown under.</summary>
    Task<HttpResponseMessage> DownloadAsync(
        string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default);

    /// <summary>
    /// The document read as a clinical summary, opened from the subject it is
    /// shown under. The catalog applies the same rules as opening the bytes:
    /// a summary is the document's contents under another name.
    /// </summary>
    Task<ClinicalSummaryView> ClinicalSummaryAsync(
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

    public async Task<ClinicalSummaryView> ClinicalSummaryAsync(
        string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}/api/v1/documents/{Uri.EscapeDataString(documentId)}/clinical-summary" +
                  $"?subjectKind={kind}&subjectId={Uri.EscapeDataString(subjectId)}";
        try
        {
            using var response = await _httpClient.GetAsync(url, ct);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return ClinicalSummaryView.Denied;

            // The catalog read the document and found nothing structured in it
            // — a PDF, say. That is an answer, not a failure.
            if (response.StatusCode is HttpStatusCode.UnprocessableEntity)
                return ClinicalSummaryView.Unreadable(await response.Content.ReadAsStringAsync(ct));

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClinicalSummaryView>(ct)
                   ?? ClinicalSummaryView.Unreadable("The document service returned no summary.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Document Service");
            throw new ServiceUnavailableException("Document Service", ex);
        }
    }

    public async Task<string?> UploadAsync(DocumentSubjectKind kind, string subjectId, string title, string type,
        Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(type), "Type");
        form.Add(new StringContent(title), "Title");
        form.Add(new StringContent(kind + ":" + subjectId), "Links");
        form.Add(new StringContent("Upload"), "SourceChannel");
        var bytes = new StreamContent(content);
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media))
            return "The selected file has no valid content type.";
        bytes.Headers.ContentType = media;
        form.Add(bytes, "file", Path.GetFileName(fileName));
        try
        {
            using var response = await _httpClient.PostAsync($"{BaseUrl()}/api/v1/documents", form, ct);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode switch {
                HttpStatusCode.Forbidden => "You may not upload documents on this record.",
                HttpStatusCode.RequestEntityTooLarge => "The document exceeds the configured upload limit.",
                HttpStatusCode.UnsupportedMediaType => "The document type or file signature is not supported.",
                HttpStatusCode.BadRequest => "The document metadata or file was refused.",
                _ => "The document service did not accept the upload. Refresh before retrying."
            };
        }
        catch (HttpRequestException) { throw new ServiceUnavailableException("Document Service", new HttpRequestException("Upload unavailable.")); }
    }

    public async Task<DocumentScanRetryResult> RetryScanAsync(string documentId, DocumentSubjectKind kind,
        string subjectId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}/api/v1/documents/{Uri.EscapeDataString(documentId)}/scan-retry" +
                  $"?subjectKind={kind}&subjectId={Uri.EscapeDataString(subjectId)}";
        try
        {
            using var response = await _httpClient.PostAsync(url, null, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, null, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                        "Scan retry was refused. Check your access to this record and refresh.",
                    HttpStatusCode.Conflict => "The document changed, is on legal hold, or no longer has a failed scan. Refresh before retrying.",
                    _ => "The document service did not confirm the retry. Refresh before retrying."
                });
            var document = await response.Content.ReadFromJsonAsync<CatalogDocumentSummary>(ct);
            return document?.ScanStatus switch
            {
                DocumentScanStatus.Clean => new(true, document.ScanStatus, "Scan completed: Clean. The document can now be opened."),
                DocumentScanStatus.Infected => new(true, document.ScanStatus, "Scan completed: Infected. The document is quarantined and cannot be opened."),
                DocumentScanStatus.Failed => new(true, document.ScanStatus, "The scan failed again. The document remains blocked. Ask an administrator to check scanner availability."),
                _ => new(false, null, "No completed scan result was returned. Refresh to check the document status.")
            };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("Document Service", new HttpRequestException("Scan retry timed out.", ex));
        }
        catch (HttpRequestException ex) { throw new ServiceUnavailableException("Document Service", ex); }
    }

    public async Task<string?> AddLinkAsync(string documentId, DocumentSubjectKind sourceKind, string sourceId,
        DocumentSubjectKind targetKind, string targetId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}/api/v1/documents/{Uri.EscapeDataString(documentId)}/links" +
                  $"?subjectKind={sourceKind}&subjectId={Uri.EscapeDataString(sourceId)}";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(url,
                new { link = targetKind + ":" + targetId.Trim() }, ct);
            if (response.IsSuccessStatusCode) return null;
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                    "The document link was refused. Check your access to both records and refresh before retrying.",
                HttpStatusCode.Conflict => "The document cannot be linked while it is on legal hold or has a conflicting change. Refresh before retrying.",
                HttpStatusCode.BadRequest => "The record reference was refused. Check its type and ID.",
                _ => "The document service did not confirm the link. Refresh before retrying."
            };
        }
        catch (HttpRequestException ex) { throw new ServiceUnavailableException("Document Service", ex); }
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
