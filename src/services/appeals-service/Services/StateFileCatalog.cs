using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;

namespace AppealsService.Services;

/// <summary>
/// Keeps every generated state file in CHO's document catalog
/// (document-service) as a <c>StateSubmission</c>, linked to its own
/// <c>StateReport</c> run, so the plan has the exact file it handed to the
/// state. The catalog owns storage, retention, legal hold and its access
/// audit. Sent as this service, naming the user who built the file in
/// <c>X-CHO-On-Behalf-Of</c>.
/// </summary>
public interface IStateFileCatalog
{
    Task<StoredStateFile> StoreAsync(StateFileUpload upload, CancellationToken ct = default);
}

public sealed record StateFileUpload(
    string TenantId,
    string OnBehalfOf,
    string RunId,
    string Title,
    byte[] Content,
    string FileName,
    string StateCode,
    IReadOnlyDictionary<string, string> ExternalIds);

public sealed record StoredStateFile(string DocumentId, string? Sha256);

public sealed class StateFileCatalogException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class HttpStateFileCatalog(IHttpClientFactory clients, ILogger<HttpStateFileCatalog> logger) : IStateFileCatalog
{
    public const string ClientName = "DocumentCatalog";
    public const string DefaultBaseUrl = "http://document-service.cloudhealthoffice";
    public const string DocumentType = "StateSubmission";
    public const string LinkKind = "StateReport";

    public async Task<StoredStateFile> StoreAsync(StateFileUpload upload, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(DocumentType), "Type" },
            { new StringContent(WebUtility.HtmlEncode(upload.Title)), "Title" },
            { new StringContent("Generated"), "SourceChannel" },
            { new StringContent($"{LinkKind}:{upload.RunId}"), "Links" },
            { new StringContent(upload.StateCode), "StateCode" },
        };
        foreach (var (key, value) in upload.ExternalIds)
            form.Add(new StringContent($"{key}={value}"), "ExternalIds");
        var file = new ByteArrayContent(upload.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", upload.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form };
        request.Headers.Add("X-Tenant-ID", upload.TenantId);
        request.Headers.Add("X-CHO-On-Behalf-Of", upload.OnBehalfOf);
        request.UseChoServiceIdentity();

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new StateFileCatalogException("document-service unreachable", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK
                && await response.Content.ReadFromJsonAsync<DocumentBody>(cancellationToken: ct) is { Id: { Length: > 0 } id } body)
                return new StoredStateFile(id, body.Sha256);

            logger.LogError("document-service answered {Status} to a state file upload", (int)response.StatusCode);
            throw new StateFileCatalogException($"document-service answered {(int)response.StatusCode}");
        }
    }

    private sealed class DocumentBody
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    }
}
