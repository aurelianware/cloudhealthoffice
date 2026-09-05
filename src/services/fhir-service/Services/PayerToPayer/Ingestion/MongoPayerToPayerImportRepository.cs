using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// MongoDB-backed <see cref="IPayerToPayerImportRepository"/> — the durable store
/// a deployment gets when <c>MongoDb:ConnectionString</c> is configured. It
/// mirrors the persistence idiom fhir-service already uses (see
/// <c>DtrService</c>): BSON documents, tenant-scoped indexes, and a unique index
/// enforcing identity.
///
/// Two collections:
///   * <c>p2p_imported_resources</c> — one document per imported resource, keyed
///     uniquely on (tenantId, importKey). The unique index is what makes a replay
///     idempotent at the STORE level, not merely in application logic: a second
///     ingestion of the same package updates the same documents.
///   * <c>p2p_import_ledger</c> — one document per exchange, keyed uniquely on
///     (tenantId, exchangeId). Committing an import is a single-document update
///     of this ledger, so a crash mid-staging leaves the member's imported
///     history unchanged (the staged rows stay invisible).
/// </summary>
public sealed class MongoPayerToPayerImportRepository : IPayerToPayerImportRepository
{
    public const string ResourceCollectionName = "p2p_imported_resources";
    public const string LedgerCollectionName = "p2p_import_ledger";

    private readonly IMongoCollection<BsonDocument> _resources;
    private readonly IMongoCollection<BsonDocument> _ledger;
    private readonly ILogger<MongoPayerToPayerImportRepository> _logger;

    public MongoPayerToPayerImportRepository(
        IMongoDatabase database, ILogger<MongoPayerToPayerImportRepository> logger)
    {
        _resources = database.GetCollection<BsonDocument>(ResourceCollectionName);
        _ledger = database.GetCollection<BsonDocument>(LedgerCollectionName);
        _logger = logger;
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        // Identity of an imported resource. Unique, so the store itself refuses a
        // duplicate rather than trusting callers to deduplicate.
        _resources.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("importKey"),
            new CreateIndexOptions { Unique = true }));

        // The read path: a member's imported history within a tenant.
        _resources.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("memberId").Ascending("exchangeId")));

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

        // Read the content hashes already held so a replay can be counted as such
        // rather than reported as newly imported data.
        var tenantId = resources[0].TenantId;
        var keys = resources.Select(r => r.ImportKey).ToList();
        var existing = await _resources
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
                Builders<BsonDocument>.Filter.In("importKey", keys)))
            .Project(Builders<BsonDocument>.Projection.Include("importKey").Include("contentHash"))
            .ToListAsync(ct);

        var heldHashes = existing.ToDictionary(
            d => d["importKey"].AsString,
            d => d.GetValue("contentHash", BsonNull.Value).IsString ? d["contentHash"].AsString : string.Empty,
            StringComparer.Ordinal);

        var written = 0;
        var unchanged = 0;
        var writes = new List<WriteModel<BsonDocument>>(resources.Count);

        foreach (var resource in resources)
        {
            if (heldHashes.TryGetValue(resource.ImportKey, out var hash)
                && string.Equals(hash, resource.ContentHash, StringComparison.Ordinal))
                unchanged++;
            else
                written++;

            writes.Add(new ReplaceOneModel<BsonDocument>(
                ResourceFilter(resource.TenantId, resource.ImportKey), ToDocument(resource)) { IsUpsert = true });
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
        var committed = await _ledger
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
                Builders<BsonDocument>.Filter.Eq("status", PayerToPayerIngestionStatus.Completed.ToString())))
            .Project(Builders<BsonDocument>.Projection.Include("exchangeId"))
            .ToListAsync(ct);

        var exchangeIds = committed.Select(d => d["exchangeId"].AsString).ToList();
        if (exchangeIds.Count == 0) return Array.Empty<ImportedFhirResource>();

        var documents = await _resources
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
                Builders<BsonDocument>.Filter.Eq("memberId", memberId),
                Builders<BsonDocument>.Filter.In("exchangeId", exchangeIds)))
            .SortBy(d => d["resourceType"]).ThenBy(d => d["sourceResourceId"])
            .ToListAsync(ct);

        return documents.Select(ToImportedResource).ToList();
    }

    // ── BSON mapping ────────────────────────────────────────────────────────────

    private static FilterDefinition<BsonDocument> ResourceFilter(string tenantId, string importKey)
        => Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
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
