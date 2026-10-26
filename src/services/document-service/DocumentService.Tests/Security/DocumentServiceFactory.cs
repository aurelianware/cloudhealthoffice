using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.DocumentStore;
using DocumentService.Models;
using DocumentService.Services;
using DocumentService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocumentService.Tests.Security;

/// <summary>
/// The real document-service pipeline (authentication, tenant, permission
/// policies, controller) over in-memory catalog, audit and storage.
/// </summary>
public sealed class DocumentServiceFactory : WebApplicationFactory<Program>
{
    public InMemoryDocumentRepository Documents { get; } = new();
    public InMemoryAccessAudit Audit { get; } = new();
    public LinkSigningStore Storage { get; } = new();
    public RetryTestScanner Scanner { get; } = new();

    /// <summary>Documents:AllowUnscannedContent, per test.</summary>
    public bool AllowUnscanned { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("MongoDb:ConnectionString", "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:MaxUploadBytes"] = "4096",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDocumentRepository>();
            services.RemoveAll<IDocumentAccessAudit>();
            services.RemoveAll<IDocumentStore>();
            services.AddSingleton<IDocumentRepository>(Documents);
            services.AddSingleton<IDocumentAccessAudit>(Audit);
            services.AddSingleton<IDocumentStore>(Storage);
            services.RemoveAll<IDocumentScanner>();
            services.AddSingleton<IDocumentScanner>(Scanner);
            // Read per request, so a test can flip it.
            services.RemoveAll<Microsoft.Extensions.Options.IOptions<global::DocumentService.Services.DocumentServiceOptions>>();
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<global::DocumentService.Services.DocumentServiceOptions>>(new LiveOptions(this));
        });
    }

    public void Reset()
    {
        Documents.Clear();
        Audit.Clear();
        Storage.Clear();
        AllowUnscanned = false;
        Scanner.Scan = null;
    }
}

public sealed class LiveOptions(DocumentServiceFactory factory) : Microsoft.Extensions.Options.IOptions<global::DocumentService.Services.DocumentServiceOptions>
{
    public global::DocumentService.Services.DocumentServiceOptions Value => new() { AllowUnscannedContent = factory.AllowUnscanned };
}

/// <summary>Catalog repository with the same revision check as the Mongo one. Stores copies, like a database.</summary>
public sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly ConcurrentDictionary<(string, string), string> _rows = new();

    private static string Save(CatalogDocument d) => JsonSerializer.Serialize(d);
    private static CatalogDocument Load(string json) => JsonSerializer.Deserialize<CatalogDocument>(json)!;

    public void Clear() => _rows.Clear();

    public IReadOnlyList<CatalogDocument> All => _rows.Values.Select(Load).ToList();

    public Task<CatalogDocument> CreateAsync(CatalogDocument document, CancellationToken ct = default)
    {
        if (!_rows.TryAdd((document.TenantId, document.Id), Save(document)))
            throw new InvalidOperationException("duplicate id");
        return Task.FromResult(document);
    }

    public Task<CatalogDocument?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue((tenantId, id), out var json) ? Load(json) : null);

    public Task<IReadOnlyList<CatalogDocument>> ListBySubjectAsync(string tenantId, DocumentLink subject, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogDocument>>(_rows
            .Where(r => r.Key.Item1 == tenantId)
            .Select(r => Load(r.Value))
            .Where(d => d.Links.Contains(subject))
            .OrderByDescending(d => d.ReceivedAt)
            .ToList());

    public Task<IReadOnlyList<CatalogDocument>> FindByHashAsync(string tenantId, string sha256, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogDocument>>(_rows
            .Where(r => r.Key.Item1 == tenantId)
            .Select(r => Load(r.Value))
            .Where(d => d.Sha256 == sha256 && d.PendingUploadPath == null)
            .OrderBy(d => d.CreatedAt)
            .ToList());

    public Task<CatalogDocument> UpdateAsync(CatalogDocument document, CancellationToken ct = default)
    {
        var key = (document.TenantId, document.Id);
        if (!_rows.TryGetValue(key, out var json) || Load(json).Revision != document.Revision)
            throw new DocumentConcurrencyException(document.Id);
        document.Revision++;
        _rows[key] = Save(document);
        return Task.FromResult(document);
    }

    public Task DeletePendingAsync(string tenantId, string id, CancellationToken ct = default)
    {
        if (_rows.TryGetValue((tenantId, id), out var json) && Load(json).PendingUploadPath != null)
            _rows.TryRemove((tenantId, id), out _);
        return Task.CompletedTask;
    }

    /// <summary>Test helper: put a document straight into the catalog.</summary>
    public void Seed(CatalogDocument document) => _rows[(document.TenantId, document.Id)] = Save(document);
}

public sealed class InMemoryAccessAudit : IDocumentAccessAudit
{
    private readonly ConcurrentQueue<DocumentAccessEvent> _entries = new();

    /// <summary>Make every write fail, as a down audit store would.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<DocumentAccessEvent> Entries => _entries.ToList();

    public void Clear()
    {
        _entries.Clear();
        Fail = false;
    }

    public Task RecordAsync(DocumentAccessEvent entry, CancellationToken ct = default)
    {
        if (Fail)
            throw new IOException("audit store unavailable");
        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentAccessEvent>> ForDocumentAsync(string tenantId, string documentId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocumentAccessEvent>>(_entries
            .Where(e => e.TenantId == tenantId && e.DocumentId == documentId)
            .OrderByDescending(e => e.At).Take(limit).ToList());
}

/// <summary>In-memory storage that can sign upload links (when <see cref="CanSign"/>), so the pre-signed flow runs end to end.</summary>
public sealed class LinkSigningStore : IDocumentStore
{
    private readonly InMemoryDocumentStore _inner = new();

    public bool CanSign { get; set; } = true;

    public InMemoryDocumentStore Inner => _inner;

    public void Clear()
    {
        _inner.Clear();
        CanSign = true;
    }

    public Task<DocumentUploadResult> UploadAsync(string container, string blobName, Stream content, string contentType, CancellationToken ct = default) =>
        _inner.UploadAsync(container, blobName, content, contentType, ct);

    public Task<DocumentUploadResult> UploadAsync(string container, string blobName, Stream content, string contentType, IReadOnlyDictionary<string, string>? tags, CancellationToken ct = default) =>
        _inner.UploadAsync(container, blobName, content, contentType, tags, ct);

    public Task<Stream> DownloadAsync(string container, string blobName, CancellationToken ct = default) => _inner.DownloadAsync(container, blobName, ct);

    public Task<bool> ExistsAsync(string container, string blobName, CancellationToken ct = default) => _inner.ExistsAsync(container, blobName, ct);

    public Task DeleteAsync(string container, string blobName, CancellationToken ct = default) => _inner.DeleteAsync(container, blobName, ct);

    public Task SetTagsAsync(string container, string blobName, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default) =>
        _inner.SetTagsAsync(container, blobName, tags, ct);

    public Task<DocumentProperties> GetPropertiesAsync(string container, string blobName, CancellationToken ct = default) =>
        _inner.GetPropertiesAsync(container, blobName, ct);

    public Task<DocumentUploadLink?> CreateUploadLinkAsync(string container, string blobName, string contentType, DateTimeOffset expiresAtUtc, CancellationToken ct = default) =>
        Task.FromResult(CanSign
            ? new DocumentUploadLink
            {
                Url = new Uri($"https://storage.example/{container}/{blobName}?sig=x"),
                Headers = new Dictionary<string, string> { ["Content-Type"] = contentType },
                ExpiresAtUtc = expiresAtUtc
            }
            : null);

    public Uri GetUri(string container, string blobName) => _inner.GetUri(container, blobName);
}

public sealed class RetryTestScanner : IDocumentScanner
{
    public Func<Stream, CancellationToken, Task<DocumentScan>>? Scan { get; set; }
    public Task<DocumentScan> ScanAsync(Stream content, string contentType, CancellationToken ct) =>
        Scan?.Invoke(content, ct) ?? Task.FromResult(new DocumentScan { Status = ScanStatus.Pending, Engine = "none" });
}
