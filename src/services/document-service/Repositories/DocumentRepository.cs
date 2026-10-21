using DocumentService.Models;
using MongoDB.Driver;

namespace DocumentService.Repositories;

public interface IDocumentRepository
{
    Task<CatalogDocument> CreateAsync(CatalogDocument document, CancellationToken ct = default);

    Task<CatalogDocument?> GetAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Finalized and pending documents linked to one subject, newest received first.</summary>
    Task<IReadOnlyList<CatalogDocument>> ListBySubjectAsync(string tenantId, DocumentLink subject, CancellationToken ct = default);

    /// <summary>A finalized document in the tenant with this content hash (duplicate detection).</summary>
    Task<CatalogDocument?> FindByHashAsync(string tenantId, string sha256, CancellationToken ct = default);

    /// <summary>Saves only if the document is still at the revision that was read; otherwise <see cref="DocumentConcurrencyException"/>.</summary>
    Task<CatalogDocument> UpdateAsync(CatalogDocument document, CancellationToken ct = default);

    /// <summary>Removes a document that never had content (an abandoned or duplicate pre-signed upload).</summary>
    Task DeletePendingAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class DocumentConcurrencyException(string id)
    : Exception($"Document {id} was changed by another request.");

public sealed class DocumentRepositoryMongo : IDocumentRepository
{
    public const string CollectionName = "CatalogDocuments";

    private readonly IMongoCollection<CatalogDocument> _collection;

    public DocumentRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<CatalogDocument>(CollectionName);

        // Every query is tenant-first. Links are an array, so the subject index is multikey.
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<CatalogDocument>(Builders<CatalogDocument>.IndexKeys
                .Ascending(x => x.TenantId).Ascending("Links.Kind").Ascending("Links.Id")),
            new CreateIndexModel<CatalogDocument>(Builders<CatalogDocument>.IndexKeys
                .Ascending(x => x.TenantId).Ascending(x => x.Sha256)),
        ]);
    }

    private static FilterDefinition<CatalogDocument> InTenant(string tenantId, string id) =>
        Builders<CatalogDocument>.Filter.Eq(x => x.TenantId, tenantId)
        & Builders<CatalogDocument>.Filter.Eq(x => x.Id, id);

    public async Task<CatalogDocument> CreateAsync(CatalogDocument document, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(document, cancellationToken: ct);
        return document;
    }

    public async Task<CatalogDocument?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _collection.Find(InTenant(tenantId, id)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CatalogDocument>> ListBySubjectAsync(string tenantId, DocumentLink subject, CancellationToken ct = default)
    {
        var filter = Builders<CatalogDocument>.Filter.Eq(x => x.TenantId, tenantId)
                     & Builders<CatalogDocument>.Filter.ElemMatch(x => x.Links,
                         l => l.Kind == subject.Kind && l.Id == subject.Id);
        return await _collection.Find(filter).SortByDescending(x => x.ReceivedAt).ToListAsync(ct);
    }

    public async Task<CatalogDocument?> FindByHashAsync(string tenantId, string sha256, CancellationToken ct = default)
    {
        var filter = Builders<CatalogDocument>.Filter.Eq(x => x.TenantId, tenantId)
                     & Builders<CatalogDocument>.Filter.Eq(x => x.Sha256, sha256)
                     & Builders<CatalogDocument>.Filter.Eq(x => x.PendingUploadPath, null);
        return await _collection.Find(filter).SortBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    }

    public async Task<CatalogDocument> UpdateAsync(CatalogDocument document, CancellationToken ct = default)
    {
        var expected = document.Revision;
        var filter = InTenant(document.TenantId, document.Id)
                     & Builders<CatalogDocument>.Filter.Eq(x => x.Revision, expected);

        document.Revision = expected + 1;
        var result = await _collection.ReplaceOneAsync(filter, document, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            document.Revision = expected;
            throw new DocumentConcurrencyException(document.Id);
        }
        return document;
    }

    public async Task DeletePendingAsync(string tenantId, string id, CancellationToken ct = default)
    {
        // Only a record that never had content: a finalized document is never deleted here.
        var filter = InTenant(tenantId, id)
                     & Builders<CatalogDocument>.Filter.Ne(x => x.PendingUploadPath, null);
        await _collection.DeleteOneAsync(filter, ct);
    }
}

/// <summary>
/// The access audit (§5): append-only. There is no update or delete here, on
/// purpose; retention of the audit itself is a deployment decision.
/// </summary>
public interface IDocumentAccessAudit
{
    Task RecordAsync(DocumentAccessEvent entry, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentAccessEvent>> ForDocumentAsync(string tenantId, string documentId, int limit, CancellationToken ct = default);
}

public sealed class DocumentAccessAuditMongo : IDocumentAccessAudit
{
    public const string CollectionName = "DocumentAccessAudit";

    private readonly IMongoCollection<DocumentAccessEvent> _collection;

    public DocumentAccessAuditMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<DocumentAccessEvent>(CollectionName);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<DocumentAccessEvent>(Builders<DocumentAccessEvent>.IndexKeys
                .Ascending(x => x.TenantId).Ascending(x => x.DocumentId).Descending(x => x.At)),
            new CreateIndexModel<DocumentAccessEvent>(Builders<DocumentAccessEvent>.IndexKeys
                .Ascending(x => x.TenantId).Ascending(x => x.SubjectKind).Ascending(x => x.SubjectId).Descending(x => x.At)),
        ]);
    }

    public Task RecordAsync(DocumentAccessEvent entry, CancellationToken ct = default) =>
        _collection.InsertOneAsync(entry, cancellationToken: ct);

    public async Task<IReadOnlyList<DocumentAccessEvent>> ForDocumentAsync(string tenantId, string documentId, int limit, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.DocumentId == documentId)
            .SortByDescending(x => x.At).Limit(limit).ToListAsync(ct);
}
