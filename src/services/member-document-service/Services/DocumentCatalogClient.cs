using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace MemberDocumentService.Services;

/// <summary>
/// Where member document files live: the document catalog (document-service,
/// docs/architecture/document-store.md). member-document-service keeps the
/// member-specific record (category, effective dates, state, related members);
/// the file, its retention, legal hold and access audit are the catalog's.
/// Every document is linked to <c>Member:{memberId}</c> (and to each related
/// member). Calls use this service's own tenant-scoped token and name the user
/// it acts for in <c>X-CHO-On-Behalf-Of</c>.
/// </summary>
public interface IDocumentCatalog
{
    Task<CatalogDocumentInfo> UploadAsync(CatalogUpload upload, CancellationToken ct = default);

    Task<CatalogUploadLink> StartUploadAsync(CatalogPresignedUpload upload, CancellationToken ct = default);

    /// <summary>Finishes a pre-signed upload. Duplicate content may come back as an existing document's id.</summary>
    Task<CatalogDocumentInfo> FinalizeAsync(string tenantId, string onBehalfOf, string documentId, CancellationToken ct = default);

    /// <summary>The file, or null when the catalog has no such document on that member (404).</summary>
    Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId, string memberId, CancellationToken ct = default);

    /// <summary>Places or releases the catalog's legal hold. False when the catalog has no such document (404).</summary>
    Task<bool> SetLegalHoldAsync(string tenantId, string onBehalfOf, string documentId, bool legalHold, string? reason, CancellationToken ct = default);
}

public sealed record CatalogUpload(
    string TenantId,
    string OnBehalfOf,
    IReadOnlyList<string> MemberIds,
    string Type,
    string Title,
    Stream Content,
    string ContentType,
    string FileName,
    string Channel,
    string? StateCode,
    DateTime? CoverageTerminationDate,
    string? RetentionPolicyId,
    bool LegalHold,
    string? LegalHoldReason,
    IReadOnlyDictionary<string, string> ExternalIds);

public sealed record CatalogPresignedUpload(
    string TenantId,
    string OnBehalfOf,
    IReadOnlyList<string> MemberIds,
    string Type,
    string Title,
    string ContentType,
    string FileName,
    string? StateCode,
    DateTime? CoverageTerminationDate,
    string? RetentionPolicyId,
    bool LegalHold,
    string? LegalHoldReason);

/// <summary>What the catalog stored. <see cref="Sha256"/> and <see cref="SizeBytes"/> are absent when it answered with only an id.</summary>
public sealed record CatalogDocumentInfo(string DocumentId, string? Sha256, long? SizeBytes);

public sealed record CatalogUploadLink(
    string DocumentId, string UploadUrl, string UploadMethod, IReadOnlyDictionary<string, string> UploadHeaders, DateTime ExpiresAtUtc);

/// <summary>
/// The catalog could not be reached (<see cref="StatusCode"/> null) or refused
/// the request (its status); nothing should be assumed done.
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
    public const string SubjectKind = "Member";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpDocumentCatalog> _logger;

    public HttpDocumentCatalog(IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpDocumentCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<CatalogDocumentInfo> UploadAsync(CatalogUpload upload, CancellationToken ct = default)
    {
        // Free text (the title) is HTML-encoded on the way out: a no-op for
        // ordinary titles, and it keeps markup out of a field the portal shows.
        // Member ids, type and state were validated by the controller.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(upload.Type), "Type" },
            { new StringContent(WebUtility.HtmlEncode(upload.Title)), "Title" },
            { new StringContent(upload.Channel), "SourceChannel" },
        };
        foreach (var memberId in upload.MemberIds)
            form.Add(new StringContent($"{SubjectKind}:{WebUtility.HtmlEncode(memberId)}"), "Links");
        foreach (var (key, value) in upload.ExternalIds)
            form.Add(new StringContent($"{key}={WebUtility.HtmlEncode(value)}"), "ExternalIds");
        if (!string.IsNullOrWhiteSpace(upload.StateCode))
            form.Add(new StringContent(WebUtility.HtmlEncode(upload.StateCode)), "StateCode");
        if (upload.CoverageTerminationDate is { } terminated)
            form.Add(new StringContent(terminated.ToString("O")), "CoverageTerminationDate");
        if (!string.IsNullOrWhiteSpace(upload.RetentionPolicyId))
            form.Add(new StringContent(WebUtility.HtmlEncode(upload.RetentionPolicyId)), "RetentionPolicyId");
        if (upload.LegalHold)
        {
            form.Add(new StringContent("true"), "LegalHold");
            if (!string.IsNullOrWhiteSpace(upload.LegalHoldReason))
                form.Add(new StringContent(WebUtility.HtmlEncode(upload.LegalHoldReason)), "LegalHoldReason");
        }

        var file = new StreamContent(upload.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        form.Add(file, "file", upload.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form };
        using var response = await SendAsync(request, upload.TenantId, upload.OnBehalfOf, ct);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            return await ReadDocumentAsync(response, ct);

        throw Refused(response, "upload", upload.TenantId);
    }

    public async Task<CatalogUploadLink> StartUploadAsync(CatalogPresignedUpload upload, CancellationToken ct = default)
    {
        var body = new StartUploadBody
        {
            Type = upload.Type,
            Title = WebUtility.HtmlEncode(upload.Title),
            ContentType = upload.ContentType,
            FileName = upload.FileName,
            Links = upload.MemberIds.Select(id => $"{SubjectKind}:{id}").ToList(),
            SourceChannel = "Upload",
            StateCode = upload.StateCode,
            CoverageTerminationDate = upload.CoverageTerminationDate,
            RetentionPolicyId = upload.RetentionPolicyId,
            LegalHold = upload.LegalHold,
            LegalHoldReason = upload.LegalHoldReason,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = JsonContent.Create(body) };
        using var response = await SendAsync(request, upload.TenantId, upload.OnBehalfOf, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw Refused(response, "pre-signed upload", upload.TenantId);

        var link = await response.Content.ReadFromJsonAsync<UploadLinkBody>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(link?.DocumentId) || string.IsNullOrWhiteSpace(link.UploadUrl))
            throw new DocumentCatalogException("document-service answered without an upload link");
        return new CatalogUploadLink(link.DocumentId, link.UploadUrl, link.UploadMethod ?? "PUT",
            link.UploadHeaders ?? new Dictionary<string, string>(), link.ExpiresAtUtc);
    }

    public async Task<CatalogDocumentInfo> FinalizeAsync(string tenantId, string onBehalfOf, string documentId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/documents/{Uri.EscapeDataString(documentId)}/finalize");
        using var response = await SendAsync(request, tenantId, onBehalfOf, ct);
        if (response.StatusCode == HttpStatusCode.OK)
            return await ReadDocumentAsync(response, ct);

        throw Refused(response, "finalize", tenantId);
    }

    public async Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId, string memberId, CancellationToken ct = default)
    {
        var path = $"api/v1/documents/{Uri.EscapeDataString(documentId)}/content"
                   + $"?subjectKind={SubjectKind}&subjectId={Uri.EscapeDataString(memberId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, tenantId, onBehalfOf, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw Refused(response, "content", tenantId);

        // Staged on local disk (deleted on close), not in memory: a document
        // can be tens of megabytes. Keeps the response's lifetime simple.
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

    public async Task<bool> SetLegalHoldAsync(string tenantId, string onBehalfOf, string documentId, bool legalHold, string? reason, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/documents/{Uri.EscapeDataString(documentId)}/legal-hold")
        {
            Content = JsonContent.Create(new HoldBody { LegalHold = legalHold, Reason = reason })
        };
        using var response = await SendAsync(request, tenantId, onBehalfOf, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw Refused(response, "legal hold", tenantId);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string tenantId, string onBehalfOf, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        request.Headers.Add(OnBehalfOfHeader, onBehalfOf);

        var tokens = ChoServiceTokens.Resolve(_services);
        var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
        if (tokens is not null && _services.GetService<ChoOutboundHosts>()?.IsChoService(target) == true)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId));
        }
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
        _logger.LogError("document-service answered {Status} to a member document {What} in tenant {TenantId}",
            (int)response.StatusCode, what, LogSanitizer.Sanitize(tenantId));
        return new DocumentCatalogException($"document-service answered {(int)response.StatusCode}", statusCode: response.StatusCode);
    }

    private static async Task<CatalogDocumentInfo> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<DocumentBody>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(body?.Id))
            throw new DocumentCatalogException("document-service answered without a document id");
        return new CatalogDocumentInfo(body.Id, body.Sha256, body.SizeBytes);
    }

    private sealed class DocumentBody
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
    }

    private sealed class UploadLinkBody
    {
        [JsonPropertyName("documentId")] public string? DocumentId { get; set; }
        [JsonPropertyName("uploadUrl")] public string? UploadUrl { get; set; }
        [JsonPropertyName("uploadMethod")] public string? UploadMethod { get; set; }
        [JsonPropertyName("uploadHeaders")] public Dictionary<string, string>? UploadHeaders { get; set; }
        [JsonPropertyName("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    }

    private sealed class StartUploadBody
    {
        public string Type { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public string? FileName { get; set; }
        public List<string> Links { get; set; } = new();
        public string? SourceChannel { get; set; }
        public string? StateCode { get; set; }
        public DateTime? CoverageTerminationDate { get; set; }
        public string? RetentionPolicyId { get; set; }
        public bool LegalHold { get; set; }
        public string? LegalHoldReason { get; set; }
    }

    private sealed class HoldBody
    {
        public bool LegalHold { get; set; }
        public string? Reason { get; set; }
    }
}
