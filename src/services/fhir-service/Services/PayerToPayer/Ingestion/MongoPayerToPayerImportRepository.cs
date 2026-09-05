using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// MongoDB-backed <see cref="IPayerToPayerImportRepository"/> — the durable store
/// a deployment gets when <c>MongoDb:ConnectionString</c> is configured. It
/// mirrors the persistence idiom fhir-service already uses (see
/// <c>DtrService</c>): it opens its own database handle from configuration, uses
/// BSON documents, and ensures its indexes once at construction. It is
/// registered as a SINGLETON for exactly that reason — a per-request instance
/// would re-issue createIndex on every call.
///
/// Two collections:
///   * <c>p2p_imported_resources</c> — one document per (exchange, imported
///     resource), keyed uniquely on (tenantId, exchangeId, importKey). Rows are
///     versioned by exchange, so staging a new exchange never overwrites or
///     hides the version an earlier exchange committed; reads take the version
///     from the most recently committed exchange for each import key.
///   * <c>p2p_import_ledger</c> — one document per exchange, keyed uniquely on
///     (tenantId, exchangeId). Committing an import is a single-document update
///     of this ledger, so a crash mid-staging leaves the member's imported
///     history exactly as the last committed exchange left it.
/// </summary>
public sealed class MongoPayerToPayerImportRepository : IPayerToPayerImportRepository
{
    public const string ResourceCollectionName = "p2p_imported_resources";
    public const string LedgerCollectionName = "p2p_import_ledger";

    private readonly IMongoCollection<BsonDocument> _resources;
    private readonly IMongoCollection<BsonDocument> _ledger;
    private readonly ILogger<MongoPayerToPayerImportRepository> _logger;

    public MongoPayerToPayerImportRepository(
        IMongoClient client, IConfiguration configuration, ILogger<MongoPayerToPayerImportRepository> logger)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "cloudhealthoffice");
        _resources = database.GetCollection<BsonDocument>(ResourceCollectionName);
        _ledger = database.GetCollection<BsonDocument>(LedgerCollectionName);
        _logger = logger;
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        // Identity of one exchange's version of an imported resource. Unique, so
        // re-staging the same exchange (a retry) rewrites its own rows and the
        // store itself refuses to write a second copy.
        _resources.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys
                .Ascending("tenantId").Ascending("exchangeId").Ascending("importKey"),
            new CreateIndexOptions { Unique = true }));

        // The read path: a member's imported history within a tenant.
        _resources.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("memberId").Ascending("importKey")));

        _ledger.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("exchangeId"),
            new CreateIndexOptions { Unique = true }));
    }

    public async Task<PayerToPayerImportLedgerEntry?> GetLedgerAsync(
        string tenantId, string exchangeId, CancellationToken ct = default)
    {
        var document = await _ledger.Find(LedgerFilter(tenantId, exchangeId)).FirstOrDefaultAsync(ct);
        return document is null ? null : ToLedgerEntry(document);
    }

    public async Task<PayerToPayerImportLedgerEntry> OpenLedgerAsync(
        PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Staging;
        entry.Failure = PayerToPayerIngestionFailure.None;
        entry.CompletedAtUtc = null;

        await _ledger.ReplaceOneAsync(
            LedgerFilter(entry.TenantId, entry.ExchangeId), ToDocument(entry),
            new ReplaceOptions { IsUpsert = true }, ct);

        return entry;
    }

    public async Task<StageOutcome> StageAsync(
        IReadOnlyList<ImportedFhirResource> resources, CancellationToken ct = default)
    {
        if (resources.Count == 0) return new StageOutcome(0, 0);

        // What is COMMITTED today for these import keys — the baseline a replay is
        // measured against. Versions staged by another in-flight exchange do not
        // count, and are not touched.
        var tenantId = resources[0].TenantId;
        var memberId = resources[0].MemberId;
        var committed = await CommittedVersionsAsync(tenantId, memberId, ct);
        var committedHashes = committed.ToDictionary(
            r => r.ImportKey, r => r.ContentHash, StringComparer.Ordinal);

        var written = 0;
        var unchanged = 0;
        var writes = new List<WriteModel<BsonDocument>>(resources.Count);

        foreach (var resource in resources)
        {
            if (committedHashes.TryGetValue(resource.ImportKey, out var hash)
                && string.Equals(hash, resource.ContentHash, StringComparison.Ordinal))
                unchanged++;
            else
                written++;

            // This exchange's own version of the resource.
            writes.Add(new ReplaceOneModel<BsonDocument>(
                ResourceFilter(resource.TenantId, resource.ExchangeId, resource.ImportKey),
                ToDocument(resource)) { IsUpsert = true });
        }

        await _resources.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
        return new StageOutcome(written, unchanged);
    }

    public async Task CommitAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Completed;
        entry.Failure = PayerToPayerIngestionFailure.None;
        entry.CompletedAtUtc = DateTime.UtcNow;

        // One document, one write: the import becomes visible atomically.
        await _ledger.ReplaceOneAsync(
            LedgerFilter(entry.TenantId, entry.ExchangeId), ToDocument(entry),
            new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task FailAsync(
        PayerToPayerImportLedgerEntry entry, PayerToPayerIngestionFailure failure, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Failed;
        entry.Failure = failure;

        await _ledger.ReplaceOneAsync(
            LedgerFilter(entry.TenantId, entry.ExchangeId), ToDocument(entry),
            new ReplaceOptions { IsUpsert = true }, ct);

        // Structured only: which exchange, which category. No member, no payload.
        _logger.LogWarning(
            "P2P import failed: exchange={Exchange} category={Failure}", Clean(entry.ExchangeId), failure);
    }

    public async Task<IReadOnlyList<ImportedFhirResource>> GetImportedResourcesAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var versions = await CommittedVersionsAsync(tenantId, memberId, ct);
        return versions
            .OrderBy(r => r.ResourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceResourceId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One row per import key: the version from the most recently committed
    /// exchange. Rows belonging to an uncommitted or failed exchange are never
    /// returned and never displace a committed version.
    /// </summary>
    private async Task<IReadOnlyList<ImportedFhirResource>> CommittedVersionsAsync(
        string tenantId, string memberId, CancellationToken ct)
    {
        var ledgerDocuments = await _ledger
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
                Builders<BsonDocument>.Filter.Eq("status", PayerToPayerIngestionStatus.Completed.ToString())))
            .Project(Builders<BsonDocument>.Projection.Include("exchangeId").Include("completedAtUtc"))
            .ToListAsync(ct);

        if (ledgerDocuments.Count == 0) return Array.Empty<ImportedFhirResource>();

        var committedAt = ledgerDocuments.ToDictionary(
            d => d["exchangeId"].AsString,
            d => d.GetValue("completedAtUtc", BsonNull.Value).IsValidDateTime
                ? d["completedAtUtc"].ToUniversalTime()
                : DateTime.MinValue,
            StringComparer.Ordinal);

        var documents = await _resources
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
                Builders<BsonDocument>.Filter.Eq("memberId", memberId),
                Builders<BsonDocument>.Filter.In("exchangeId", committedAt.Keys)))
            .ToListAsync(ct);

        return documents
            .Select(ToImportedResource)
            .GroupBy(r => r.ImportKey, StringComparer.Ordinal)
            // Deterministic winner: latest commit, then latest ingest, then id.
            .Select(g => g
                .OrderByDescending(r => committedAt[r.ExchangeId])
                .ThenByDescending(r => r.IngestedAtUtc)
                .ThenBy(r => r.ExchangeId, StringComparer.Ordinal)
                .First())
            .ToList();
    }

    // ── BSON mapping ────────────────────────────────────────────────────────────

    private static FilterDefinition<BsonDocument> ResourceFilter(
        string tenantId, string exchangeId, string importKey)
        => Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
            Builders<BsonDocument>.Filter.Eq("exchangeId", exchangeId),
            Builders<BsonDocument>.Filter.Eq("importKey", importKey));

    private static FilterDefinition<BsonDocument> LedgerFilter(string tenantId, string exchangeId)
        => Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
            Builders<BsonDocument>.Filter.Eq("exchangeId", exchangeId));

    private static BsonDocument ToDocument(ImportedFhirResource resource) => new()
    {
        ["tenantId"] = resource.TenantId,
        ["importKey"] = resource.ImportKey,
        ["memberId"] = resource.MemberId,
        ["sourcePayerId"] = resource.SourcePayerId,
        ["sourceEndpointKey"] = resource.SourceEndpointKey ?? (BsonValue)BsonNull.Value,
        ["exchangeId"] = resource.ExchangeId,
        ["resourceType"] = resource.ResourceType,
        ["sourceResourceId"] = resource.SourceResourceId,
        ["remoteMemberId"] = resource.RemoteMemberId,
        ["classification"] = resource.Classification.ToString(),
        ["resourceJson"] = resource.ResourceJson,
        ["contentHash"] = resource.ContentHash,
        ["referencesNormalized"] = resource.ReferencesNormalized,
        ["receivedAtUtc"] = resource.ReceivedAtUtc,
        ["ingestedAtUtc"] = resource.IngestedAtUtc,
    };

    private static ImportedFhirResource ToImportedResource(BsonDocument d) => new()
    {
        TenantId = d["tenantId"].AsString,
        ImportKey = d["importKey"].AsString,
        MemberId = d["memberId"].AsString,
        SourcePayerId = d["sourcePayerId"].AsString,
        SourceEndpointKey = d.GetValue("sourceEndpointKey", BsonNull.Value).IsString
            ? d["sourceEndpointKey"].AsString : null,
        ExchangeId = d["exchangeId"].AsString,
        ResourceType = d["resourceType"].AsString,
        SourceResourceId = d["sourceResourceId"].AsString,
        RemoteMemberId = d["remoteMemberId"].AsString,
        Classification = Enum.TryParse<ImportedResourceClass>(
            d.GetValue("classification", string.Empty).AsString, out var c) ? c : ImportedResourceClass.Unsupported,
        ResourceJson = d["resourceJson"].AsString,
        ContentHash = d["contentHash"].AsString,
        ReferencesNormalized = d.GetValue("referencesNormalized", false).ToBoolean(),
        ReceivedAtUtc = d["receivedAtUtc"].ToUniversalTime(),
        IngestedAtUtc = d["ingestedAtUtc"].ToUniversalTime(),
    };

    private static BsonDocument ToDocument(PayerToPayerImportLedgerEntry entry) => new()
    {
        ["tenantId"] = entry.TenantId,
        ["exchangeId"] = entry.ExchangeId,
        ["memberId"] = entry.MemberId,
        ["sourcePayerId"] = entry.SourcePayerId,
        ["status"] = entry.Status.ToString(),
        ["failure"] = entry.Failure.ToString(),
        ["archivedPackageJson"] = entry.ArchivedPackageJson ?? (BsonValue)BsonNull.Value,
        ["counts"] = new BsonDocument
        {
            ["received"] = entry.Counts.Received,
            ["persisted"] = entry.Counts.Persisted,
            ["administrativeReference"] = entry.Counts.AdministrativeReference,
            ["duplicate"] = entry.Counts.Duplicate,
            ["unsupported"] = entry.Counts.Unsupported,
            ["unsupportedTypes"] = new BsonArray(entry.Counts.UnsupportedTypes),
            ["referencesNormalized"] = entry.Counts.ReferencesNormalized,
        },
        ["startedAtUtc"] = entry.StartedAtUtc,
        ["completedAtUtc"] = entry.CompletedAtUtc ?? (BsonValue)BsonNull.Value,
    };

    private static PayerToPayerImportLedgerEntry ToLedgerEntry(BsonDocument d)
    {
        var counts = d.GetValue("counts", new BsonDocument()).AsBsonDocument;
        return new PayerToPayerImportLedgerEntry
        {
            TenantId = d["tenantId"].AsString,
            ExchangeId = d["exchangeId"].AsString,
            MemberId = d["memberId"].AsString,
            SourcePayerId = d["sourcePayerId"].AsString,
            Status = Enum.TryParse<PayerToPayerIngestionStatus>(
                d.GetValue("status", string.Empty).AsString, out var s) ? s : PayerToPayerIngestionStatus.NotStarted,
            Failure = Enum.TryParse<PayerToPayerIngestionFailure>(
                d.GetValue("failure", string.Empty).AsString, out var f) ? f : PayerToPayerIngestionFailure.None,
            ArchivedPackageJson = d.GetValue("archivedPackageJson", BsonNull.Value).IsString
                ? d["archivedPackageJson"].AsString : null,
            Counts = new PayerToPayerIngestionCounts
            {
                Received = counts.GetValue("received", 0).ToInt32(),
                Persisted = counts.GetValue("persisted", 0).ToInt32(),
                AdministrativeReference = counts.GetValue("administrativeReference", 0).ToInt32(),
                Duplicate = counts.GetValue("duplicate", 0).ToInt32(),
                Unsupported = counts.GetValue("unsupported", 0).ToInt32(),
                UnsupportedTypes = counts.GetValue("unsupportedTypes", new BsonArray()).AsBsonArray
                    .Select(v => v.AsString).ToList(),
                ReferencesNormalized = counts.GetValue("referencesNormalized", 0).ToInt32(),
            },
            StartedAtUtc = d["startedAtUtc"].ToUniversalTime(),
            CompletedAtUtc = d.GetValue("completedAtUtc", BsonNull.Value).IsValidDateTime
                ? d["completedAtUtc"].ToUniversalTime() : null,
        };
    }

    /// <summary>Strips CR/LF so an id cannot forge a log entry (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
