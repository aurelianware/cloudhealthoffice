using System.Net;
using System.Security.Cryptography;
using MemberDocumentService.Services;

namespace MemberDocumentService.Tests.Support;

/// <summary>
/// An in-memory document catalog: records every call, answers like
/// document-service, and can be told to refuse an operation with a status
/// (or to be unreachable, status null).
/// </summary>
public sealed class FakeDocumentCatalog : IDocumentCatalog
{
    public static readonly byte[] DefaultContent = "%PDF-1.7\n1 0 obj << >> endobj\n%%EOF"u8.ToArray();

    private readonly object _gate = new();
    private int _next;

    public List<(CatalogUpload Upload, byte[] Bytes)> Uploads { get; } = new();
    public List<CatalogPresignedUpload> StartedUploads { get; } = new();
    public List<(string TenantId, string OnBehalfOf, string DocumentId)> Finalized { get; } = new();
    public List<(string TenantId, string OnBehalfOf, string DocumentId, string MemberId)> Downloads { get; } = new();
    public List<(string TenantId, string OnBehalfOf, string DocumentId, bool LegalHold, string? Reason)> Holds { get; } = new();

    /// <summary>Operation name ("upload", "start", "finalize", "download", "hold") → the status to refuse with; null value = unreachable.</summary>
    public Dictionary<string, HttpStatusCode?> Failures { get; } = new();

    /// <summary>What finalize answers; defaults to the same id with the default content's hash and size.</summary>
    public Func<string, CatalogDocumentInfo>? FinalizeResult { get; set; }

    /// <summary>What a download returns; null = the catalog has no such document on that member.</summary>
    public Func<byte[]?> Content { get; set; } = () => DefaultContent;

    /// <summary>Whether the catalog knows the document a hold names.</summary>
    public bool HoldTargetExists { get; set; } = true;

    public void Reset()
    {
        lock (_gate)
        {
            Uploads.Clear();
            StartedUploads.Clear();
            Finalized.Clear();
            Downloads.Clear();
            Holds.Clear();
            Failures.Clear();
            FinalizeResult = null;
            Content = () => DefaultContent;
            HoldTargetExists = true;
            _next = 0;
        }
    }

    public bool NothingCalled =>
        Uploads.Count == 0 && StartedUploads.Count == 0 && Finalized.Count == 0 && Downloads.Count == 0 && Holds.Count == 0;

    public async Task<CatalogDocumentInfo> UploadAsync(CatalogUpload upload, CancellationToken ct = default)
    {
        ThrowIfFailing("upload");
        using var copy = new MemoryStream();
        await upload.Content.CopyToAsync(copy, ct);
        var bytes = copy.ToArray();
        lock (_gate) Uploads.Add((upload, bytes));
        return new CatalogDocumentInfo(NextId(), Sha256(bytes), bytes.Length);
    }

    public Task<CatalogUploadLink> StartUploadAsync(CatalogPresignedUpload upload, CancellationToken ct = default)
    {
        ThrowIfFailing("start");
        lock (_gate) StartedUploads.Add(upload);
        return Task.FromResult(new CatalogUploadLink(
            NextId(),
            "https://storage.example/documents/staged?sig=x",
            "PUT",
            new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = upload.ContentType },
            DateTime.UtcNow.AddMinutes(15)));
    }

    public Task<CatalogDocumentInfo> FinalizeAsync(string tenantId, string onBehalfOf, string documentId, CancellationToken ct = default)
    {
        ThrowIfFailing("finalize");
        lock (_gate) Finalized.Add((tenantId, onBehalfOf, documentId));
        return Task.FromResult(FinalizeResult?.Invoke(documentId)
                               ?? new CatalogDocumentInfo(documentId, Sha256(DefaultContent), DefaultContent.Length));
    }

    public Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId, string memberId, CancellationToken ct = default)
    {
        ThrowIfFailing("download");
        lock (_gate) Downloads.Add((tenantId, onBehalfOf, documentId, memberId));
        var bytes = Content();
        return Task.FromResult<Stream?>(bytes is null ? null : new MemoryStream(bytes));
    }

    public Task<bool> SetLegalHoldAsync(string tenantId, string onBehalfOf, string documentId, bool legalHold, string? reason, CancellationToken ct = default)
    {
        ThrowIfFailing("hold");
        lock (_gate) Holds.Add((tenantId, onBehalfOf, documentId, legalHold, reason));
        return Task.FromResult(HoldTargetExists);
    }

    private void ThrowIfFailing(string operation)
    {
        if (Failures.TryGetValue(operation, out var status))
            throw new DocumentCatalogException($"fake catalog refused {operation}", statusCode: status);
    }

    private string NextId() => $"cat-{Interlocked.Increment(ref _next)}";

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
