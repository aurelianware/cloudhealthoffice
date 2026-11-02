using System.Collections.Concurrent;
using AppealsService.Services;

namespace AppealsService.Tests.Fakes;

/// <summary>Records what would have been kept in the document catalog; can be told to fail.</summary>
public sealed class RecordingStateFileCatalog : IStateFileCatalog
{
    public ConcurrentQueue<StateFileUpload> Stored { get; } = new();
    public bool Fail { get; set; }

    public Task<StoredStateFile> StoreAsync(StateFileUpload upload, CancellationToken ct = default)
    {
        if (Fail) throw new StateFileCatalogException("document-service answered 503");
        Stored.Enqueue(upload);
        return Task.FromResult(new StoredStateFile("doc-" + Stored.Count, null));
    }

    public void Clear()
    {
        Stored.Clear();
        Fail = false;
    }
}
