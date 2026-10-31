using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// Keeps every file a drop run loads, exactly as received, in CHO's document
/// repository (document-service): type <c>SourceExtract</c>, channel
/// <c>SFTP</c>, linked to the run (<c>ImportRun:{runId}</c>, readable with
/// <c>claims:import</c>), under the tenant's retention policy (ten years by
/// default) and open to legal hold. The drop bucket is transit only; this is
/// the record of what the plan's systems sent and when.
/// </summary>
public interface ICpcdsDropArchive
{
    Task<CpcdsDropArchiveResult> ArchiveAsync(
        string tenantId, string runId, CpcdsDropFile file, string content, string? sourceSystem, CancellationToken ct);
}

/// <summary>The archived document's id, or why the file could not be archived.</summary>
public sealed record CpcdsDropArchiveResult(string? DocumentId, string? Error);

public sealed class CpcdsDropArchiveOptions
{
    /// <summary>Off unless document-service runs in this deployment.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Files up to this size go in one multipart upload; larger ones use
    /// document-service's pre-signed upload (which needs storage that can sign
    /// upload links). Document-service's own multipart limit is 64 MiB plus form overhead.
    /// </summary>
    public long MultipartMaxBytes { get; set; } = 60L * 1024 * 1024;
}

public sealed class HttpCpcdsDropArchive : ICpcdsDropArchive
{
    public const string DocumentClientName = "DocumentServiceArchive";

    /// <summary>For the pre-signed PUT: a storage URL, never a CHO service, so no CHO token.</summary>
    public const string StorageClientName = "DocumentStorageUpload";

    public const string DocumentType = "SourceExtract";
    public const string Channel = "SFTP";

    private readonly IHttpClientFactory _http;
    private readonly CpcdsDropArchiveOptions _options;

    public HttpCpcdsDropArchive(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<CpcdsDropOptions> options)
    {
        _http = http;
        _options = options.Value.Archive;
    }

    /// <summary>X12 when the content starts with an ISA segment; otherwise CSV, or plain text for a .txt.</summary>
    public static string ContentTypeFor(string fileName, string content)
    {
        if (content.TrimStart('﻿').StartsWith("ISA", StringComparison.Ordinal))
            return "application/edi-x12";
        return Path.GetExtension(fileName).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "text/csv";
    }

    public async Task<CpcdsDropArchiveResult> ArchiveAsync(
        string tenantId, string runId, CpcdsDropFile file, string content, string? sourceSystem, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var contentType = ContentTypeFor(file.Name, content);
        try
        {
            return bytes.LongLength <= _options.MultipartMaxBytes
                ? await MultipartAsync(tenantId, runId, file, bytes, contentType, sourceSystem, ct)
                : await PresignedAsync(tenantId, runId, file, bytes, contentType, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return new CpcdsDropArchiveResult(null, $"document-service could not be reached: {ex.GetType().Name}.");
        }
    }

    private async Task<CpcdsDropArchiveResult> MultipartAsync(
        string tenantId, string runId, CpcdsDropFile file, byte[] bytes, string contentType, string? sourceSystem, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(body, "file", file.Name);
        form.Add(new StringContent(DocumentType), "Type");
        form.Add(new StringContent(file.Name), "Title");
        form.Add(new StringContent($"ImportRun:{runId}"), "Links");
        form.Add(new StringContent(Channel), "SourceChannel");
        form.Add(new StringContent(file.LastWriteUtc.ToString("O")), "ReceivedAt");
        form.Add(new StringContent($"DropRun={runId}"), "ExternalIds");
        if (Token(sourceSystem) is { } system)
            form.Add(new StringContent($"SourceSystem={system}"), "ExternalIds");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents") { Content = form }.UseChoServiceIdentity();
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await _http.CreateClient(DocumentClientName).SendAsync(request, ct);
        return await ResultAsync(response, ct);
    }

    private async Task<CpcdsDropArchiveResult> PresignedAsync(
        string tenantId, string runId, CpcdsDropFile file, byte[] bytes, string contentType, CancellationToken ct)
    {
        var client = _http.CreateClient(DocumentClientName);
        using var start = new HttpRequestMessage(HttpMethod.Post, "api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                type = DocumentType,
                title = file.Name,
                contentType,
                fileName = file.Name,
                links = new[] { $"ImportRun:{runId}" },
                sourceChannel = Channel,
                receivedAt = file.LastWriteUtc,
            }),
        }.UseChoServiceIdentity();
        start.Headers.Add("X-Tenant-ID", tenantId);
        using var started = await client.SendAsync(start, ct);
        if (!started.IsSuccessStatusCode)
            return await ResultAsync(started, ct);

        using var link = JsonDocument.Parse(await started.Content.ReadAsStringAsync(ct));
        var root = link.RootElement;
        var documentId = root.GetProperty("documentId").GetString()!;
        using (var put = new HttpRequestMessage(
                   new HttpMethod(root.TryGetProperty("uploadMethod", out var m) ? m.GetString() ?? "PUT" : "PUT"),
                   root.GetProperty("uploadUrl").GetString()))
        {
            put.Content = new ByteArrayContent(bytes);
            if (root.TryGetProperty("uploadHeaders", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (!put.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                        put.Content.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                }
            }
            using var uploaded = await _http.CreateClient(StorageClientName).SendAsync(put, ct);
            if (!uploaded.IsSuccessStatusCode)
                return new CpcdsDropArchiveResult(null, $"The upload to document storage answered {(int)uploaded.StatusCode}.");
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Post, $"api/v1/documents/{Uri.EscapeDataString(documentId)}/finalize")
            .UseChoServiceIdentity();
        finalize.Headers.Add("X-Tenant-ID", tenantId);
        using var finalized = await client.SendAsync(finalize, ct);
        return await ResultAsync(finalized, ct);
    }

    private static async Task<CpcdsDropArchiveResult> ResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(text) || text.Length > 300 ? string.Empty : $": {text.Trim()}";
            return new CpcdsDropArchiveResult(null, $"document-service answered {(int)response.StatusCode}{detail}");
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } documentId
            ? new CpcdsDropArchiveResult(documentId, null)
            : new CpcdsDropArchiveResult(null, "document-service did not return a document id.");
    }

    /// <summary>An ExternalIds value is one token: letters, digits, '-', '_' and '.' only.</summary>
    private static string? Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = new string(value.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return token.Length is > 0 and <= 64 ? token : null;
    }
}
