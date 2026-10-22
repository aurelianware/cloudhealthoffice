using System.Net;
using CorrespondenceService.Services;

namespace CorrespondenceService.Tests.Support;

public sealed class FakeLetterDocumentStore : ILetterDocumentStore
{
    private int _next;
    public List<LetterDocumentUpload> Uploads { get; } = new();
    public List<(string TenantId, string OnBehalfOf, string DocumentId, string Kind, string Id)> Opens { get; } = new();

    /// <summary>Refuse uploads with this status; null with <see cref="Fail"/> set means unreachable.</summary>
    public HttpStatusCode? FailStatus { get; set; }
    public bool Fail { get; set; }

    public void Reset() { Uploads.Clear(); Opens.Clear(); FailStatus = null; Fail = false; _next = 0; }

    public Task<StoredLetterDocument> StoreAsync(LetterDocumentUpload upload, CancellationToken ct = default)
    {
        if (Fail) throw new DocumentCatalogException("refused", statusCode: FailStatus);
        lock (Uploads) Uploads.Add(upload);
        return Task.FromResult(new StoredLetterDocument($"doc-{Interlocked.Increment(ref _next)}", "abc"));
    }

    public Task<Stream?> OpenAsync(string tenantId, string onBehalfOf, string documentId, string subjectKind, string subjectId, CancellationToken ct = default)
    {
        lock (Opens) Opens.Add((tenantId, onBehalfOf, documentId, subjectKind, subjectId));
        var upload = Uploads.LastOrDefault();
        return Task.FromResult<Stream?>(upload is null ? null : new MemoryStream(upload.Pdf));
    }
}
