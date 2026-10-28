using CloudHealthOffice.DocumentStore;

namespace FhirService.Services.BulkExport;

/// <summary>
/// Where completed exports' NDJSON files live (Q-18): the shared
/// <see cref="IDocumentStore"/> abstraction, but its own store, configured
/// under <c>BulkExport:DocumentStore:*</c> and never the documents bucket.
/// Files are only reached through this, by the key the job records; nothing
/// hands a client a direct storage URL.
/// </summary>
public interface IBulkExportFileStore
{
    /// <summary>Writes one export file and returns the key it was stored under.</summary>
    Task<string> UploadAsync(string tenantId, string jobId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>The file's content, or null when it is gone.</summary>
    Task<Stream?> OpenAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes a file. A file that is already gone is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class DocumentStoreBulkExportFileStore : IBulkExportFileStore
{
    public const string ContentType = "application/fhir+ndjson";

    private readonly IDocumentStore _store;

    public DocumentStoreBulkExportFileStore(IDocumentStore store) => _store = store;

    /// <summary>
    /// The store from <c>BulkExport:DocumentStore:*</c>. Read relative to the
    /// <c>BulkExport</c> section, so neither the documents store's settings nor
    /// its legacy <c>BlobStorage:ConnectionString</c> can be picked up by
    /// accident and put member exports in the documents bucket.
    /// </summary>
    public static DocumentStoreBulkExportFileStore Create(
        IConfiguration configuration, string environmentName, ILoggerFactory loggerFactory)
        => new(DocumentStoreServiceCollectionExtensions.Create(
            configuration.GetSection(BulkExportOptions.SectionName), environmentName, loggerFactory));

    public static string KeyFor(string tenantId, string jobId, string fileName)
        => $"tenants/{tenantId}/{jobId}/{fileName}";

    public async Task<string> UploadAsync(
        string tenantId, string jobId, string fileName, Stream content, CancellationToken ct = default)
    {
        var key = KeyFor(tenantId, jobId, fileName);
        await _store.UploadAsync(BulkExportOptions.Container, key, content, ContentType, ct);
        return key;
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        try
        {
            return await _store.DownloadAsync(BulkExportOptions.Container, key, ct);
        }
        catch (DocumentNotFoundException)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => _store.DeleteAsync(BulkExportOptions.Container, key, ct);
}
