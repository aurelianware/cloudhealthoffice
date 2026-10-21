using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace AttachmentService.Services;

/// <summary>
/// Where attachment files live: the document catalog (document-service, see
/// docs/architecture/document-store.md). attachment-service keeps the X12
/// 275 record (control numbers, acknowledgments); the file, its links and
/// its access audit are the catalog's. Calls use this service's own token,
/// tenant-scoped, and name the user it acts for (X-CHO-On-Behalf-Of) so the
/// catalog's audit shows who it was really for.
/// </summary>
public interface IDocumentCatalog
{
    Task<CatalogUploadResult> UploadAsync(CatalogUpload upload, CancellationToken ct = default);

    /// <summary>The file, or null when the catalog has no such document on that subject (404).</summary>
    Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId, AttachmentParentType subjectType, string subjectId, CancellationToken ct = default);
}

public sealed record CatalogUpload(
    string TenantId,
    string OnBehalfOf,
    AttachmentParentType SubjectType,
    string SubjectId,
    string Title,
    Stream Content,
    string ContentType,
    string FileName,
    string Channel,
    IReadOnlyDictionary<string, string> ExternalIds);

public sealed record CatalogUploadResult(string DocumentId);

/// <summary>
/// The catalog could not be reached (<see cref="StatusCode"/> null) or refused
/// the request (its status); nothing should be assumed stored.
/// </summary>
public sealed class DocumentCatalogException(string message, Exception? inner = null, HttpStatusCode? statusCode = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class HttpDocumentCatalog : IDocumentCatalog
{
    public const string ClientName = "DocumentService";
    public const string DefaultBaseUrl = "http://document-service.cloudhealthoffice";
    public const string OnBehalfOfHeader = "X-CHO-On-Behalf-Of";

    /// <summary>Attachments are filed as this catalog type; the 275's own document type becomes the title.</summary>
    public const string CatalogType = "Other";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpDocumentCatalog> _logger;

    public HttpDocumentCatalog(IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpDocumentCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// What a user needs, in their own name, for the catalog's subject check:
    /// attachment-service calls with its service token, which the catalog
    /// trusts, so it enforces the same rule itself before calling.
    /// </summary>
    public static string SubjectReadPermission(AttachmentParentType type) => type switch
    {
        AttachmentParentType.Claim => "claims:read",
        AttachmentParentType.Authorization => "authorizations:read",
        _ => "appeals:read",
    };

    public static string SubjectKind(AttachmentParentType type) => type switch
    {
        AttachmentParentType.Claim => "Claim",
        AttachmentParentType.Authorization => "Authorization",
        _ => "Appeal",
    };

    public async Task<CatalogUploadResult> UploadAsync(CatalogUpload upload, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(ClientName);

        // The title is the 275's free-text document type and the subject id a
        // validated plain id. Both are HTML-encoded on the way out: a no-op for
        // ids and ordinary titles, and it keeps markup out of a field the
        // portal shows.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(CatalogType), "Type" },
            { new StringContent(WebUtility.HtmlEncode(upload.Title)), "Title" },
            { new StringContent($"{SubjectKind(upload.SubjectType)}:{WebUtility.HtmlEncode(upload.SubjectId)}"), "Links" },
            { new StringContent(upload.Channel), "SourceChannel" },
        };
        foreach (var (key, value) in upload.ExternalIds)
            form.Add(new StringContent($"{key}={value}"), "ExternalIds");

        var file = new StreamContent(upload.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        form.Add(file, "file", upload.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form };
        await PrepareAsync(request, client, upload.TenantId, upload.OnBehalfOf);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DocumentCatalogException("document-service unreachable", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<UploadedDocument>(cancellationToken: ct);
                if (string.IsNullOrWhiteSpace(body?.Id))
                    throw new DocumentCatalogException("document-service answered without a document id");
                return new CatalogUploadResult(body.Id);
            }

            _logger.LogError("document-service refused an attachment upload for tenant {TenantId}: {Status}",
                LogSanitizer.Sanitize(upload.TenantId), (int)response.StatusCode);
            throw new DocumentCatalogException($"document-service answered {(int)response.StatusCode}", statusCode: response.StatusCode);
        }
    }

    public async Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId,
        AttachmentParentType subjectType, string subjectId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        var path = $"api/v1/documents/{Uri.EscapeDataString(documentId)}/content"
                   + $"?subjectKind={SubjectKind(subjectType)}&subjectId={Uri.EscapeDataString(subjectId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        await PrepareAsync(request, client, tenantId, onBehalfOf);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DocumentCatalogException("document-service unreachable", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("document-service answered {Status} for attachment content in tenant {TenantId}",
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId));
                throw new DocumentCatalogException($"document-service answered {(int)response.StatusCode}", statusCode: response.StatusCode);
            }

            // Staged on local disk (deleted on close), not in memory: an
            // attachment can be tens of megabytes. Keeps the response's
            // lifetime simple.
            var buffer = new FileStream(
                Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await response.Content.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                return buffer;
            }
            catch
            {
                await buffer.DisposeAsync();
                throw;
            }
        }
    }

    private async Task PrepareAsync(HttpRequestMessage request, HttpClient client, string tenantId, string onBehalfOf)
    {
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        request.Headers.Add(OnBehalfOfHeader, onBehalfOf);

        var tokens = ChoServiceTokens.Resolve(_services);
        var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
        if (tokens is null || _services.GetService<ChoOutboundHosts>()?.IsChoService(target) != true)
        {
            return; // the shared handler forwards the caller's token (CHO hosts only)
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId));
    }

    private sealed class UploadedDocument
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
