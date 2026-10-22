using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace CorrespondenceService.Services;

/// <summary>
/// Rendered letters are stored write-once in the document catalog
/// (document-service, docs/architecture/document-store.md) as type
/// <c>OutboundLetter</c>, linked to every subject of the letter. The catalog
/// owns storage, retention, legal hold and the access audit. Calls use this
/// service's tenant-scoped token and name the user in <c>X-CHO-On-Behalf-Of</c>,
/// so the catalog applies that user's permissions to every link.
/// </summary>
public interface ILetterDocumentStore
{
    Task<StoredLetterDocument> StoreAsync(LetterDocumentUpload upload, CancellationToken ct = default);

    /// <summary>The PDF, opened from one of its subjects; null when the catalog has no such document on that subject.</summary>
    Task<Stream?> OpenAsync(string tenantId, string onBehalfOf, string documentId, string subjectKind, string subjectId, CancellationToken ct = default);
}

public sealed record LetterDocumentUpload(
    string TenantId,
    string OnBehalfOf,
    IReadOnlyList<(string Kind, string Id)> Links,
    string DocumentType,
    string Title,
    byte[] Pdf,
    string FileName,
    string? StateCode,
    IReadOnlyDictionary<string, string> ExternalIds);

public sealed record StoredLetterDocument(string DocumentId, string? Sha256);

public sealed class DocumentCatalogException(string message, Exception? inner = null, HttpStatusCode? statusCode = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class HttpLetterDocumentStore : ILetterDocumentStore
{
    public const string ClientName = "DocumentService";
    public const string DefaultBaseUrl = "http://document-service.cloudhealthoffice";
    public const string OnBehalfOfHeader = "X-CHO-On-Behalf-Of";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpLetterDocumentStore> _logger;

    public HttpLetterDocumentStore(IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpLetterDocumentStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<StoredLetterDocument> StoreAsync(LetterDocumentUpload upload, CancellationToken ct = default)
    {
        // Caller-derived values are HTML-encoded on the way out: a no-op for
        // the validated ids and ordinary titles, and it keeps markup out of
        // fields the portal shows.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(WebUtility.HtmlEncode(upload.DocumentType)), "Type" },
            { new StringContent(WebUtility.HtmlEncode(upload.Title)), "Title" },
            { new StringContent("Generated"), "SourceChannel" },
        };
        foreach (var (kind, id) in upload.Links)
            form.Add(new StringContent($"{kind}:{WebUtility.HtmlEncode(id)}"), "Links");
        foreach (var (key, value) in upload.ExternalIds)
            form.Add(new StringContent($"{key}={WebUtility.HtmlEncode(value)}"), "ExternalIds");
        if (!string.IsNullOrWhiteSpace(upload.StateCode))
            form.Add(new StringContent(WebUtility.HtmlEncode(upload.StateCode)), "StateCode");

        var file = new ByteArrayContent(upload.Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", upload.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form };
        using var response = await SendAsync(request, upload.TenantId, upload.OnBehalfOf, ct);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var body = await response.Content.ReadFromJsonAsync<DocumentBody>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(body?.Id))
                throw new DocumentCatalogException("document-service answered without a document id");
            return new StoredLetterDocument(body.Id, body.Sha256);
        }
        throw Refused(response, "letter upload", upload.TenantId);
    }

    public async Task<Stream?> OpenAsync(string tenantId, string onBehalfOf, string documentId, string subjectKind, string subjectId, CancellationToken ct = default)
    {
        var path = $"api/v1/documents/{Uri.EscapeDataString(documentId)}/content"
                   + $"?subjectKind={Uri.EscapeDataString(subjectKind)}&subjectId={Uri.EscapeDataString(subjectId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, tenantId, onBehalfOf, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            return null;
        if (!response.IsSuccessStatusCode)
            throw Refused(response, "letter content", tenantId);

        // Letters are small (tens to hundreds of KB): buffer in memory.
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string tenantId, string onBehalfOf, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        request.Headers.Add(OnBehalfOfHeader, onBehalfOf);

        var tokens = ChoServiceTokens.Resolve(_services);
        var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
        if (tokens is not null && _services.GetService<ChoOutboundHosts>()?.IsChoService(target) == true)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId));
        // Otherwise the shared handler forwards the caller's token (CHO hosts only).

        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DocumentCatalogException("document-service unreachable", ex);
        }
    }

    private DocumentCatalogException Refused(HttpResponseMessage response, string what, string tenantId)
    {
        _logger.LogError("document-service answered {Status} to a {What} in tenant {TenantId}",
            (int)response.StatusCode, what, LogSanitizer.Sanitize(tenantId));
        return new DocumentCatalogException($"document-service answered {(int)response.StatusCode}", statusCode: response.StatusCode);
    }

    private sealed class DocumentBody
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    }
}
