using System.Collections.Concurrent;
using FhirService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services;

/// <summary>
/// One coverage assertion CHO made through CRD: what the
/// <c>coverage-information</c> extension it returned said, keyed by its
/// <c>coverage-assertion-id</c>.
///
/// <para>
/// Kept so that a DTR client can hand the id back as the <c>context</c> of
/// <c>$questionnaire-package</c> (DTR spec-107) and CHO can tell which forms it
/// asked for, and so CHO retains the guidance it gave (CRD found-11). CRD
/// sec-6 permits keeping CRD data for audit and DTR context, and nothing else
/// reads it.
/// </para>
///
/// <para>
/// Only references and codes are kept, never the draft order or patient: the
/// Coverage and order references are the EHR's own ids, which is what the
/// package's draft QuestionnaireResponse must point at anyway (spec-139).
/// </para>
/// </summary>
public sealed record CrdCoverageAssertion
{
    public required string TenantId { get; init; }

    /// <summary>The <c>coverage-assertion-id</c> CHO returned.</summary>
    public required string AssertionId { get; init; }

    /// <summary>The EHR's Coverage the assertion was made against, e.g. <c>Coverage/123</c>.</summary>
    public required string CoverageReference { get; init; }

    /// <summary>The draft order the assertion is about, e.g. <c>ServiceRequest/abc</c>, when it had an id.</summary>
    public string? OrderReference { get; init; }

    public string? BillingCodeSystem { get; init; }
    public string? BillingCode { get; init; }

    public required CrdDetermination Determination { get; init; }

    /// <summary>True when the assertion said documentation is needed (doc-needed).</summary>
    public bool DocumentationNeeded => Determination == CrdDetermination.DocumentationRequired;

    /// <summary>Canonicals (<c>url|version</c>) of the questionnaires the assertion named.</summary>
    public IReadOnlyList<string> Questionnaires { get; init; } = [];

    public required DateTimeOffset AssertedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The coverage assertions CRD returned, readable by tenant and assertion id
/// until they expire.
/// </summary>
public interface ICrdAssertionStore
{
    Task SaveAsync(IReadOnlyList<CrdCoverageAssertion> assertions, CancellationToken ct = default);

    /// <summary>The tenant's assertion with this id, while it has not expired at <paramref name="nowUtc"/>.</summary>
    Task<CrdCoverageAssertion?> FindAsync(
        string tenantId, string assertionId, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// MongoDB store (<c>crd_coverage_assertions</c>), unique on (tenantId,
/// assertionId). Expired assertions are removed by a TTL index and never
/// answered before that removal runs. Singleton: indexes are ensured once.
/// </summary>
public sealed class MongoCrdAssertionStore : ICrdAssertionStore
{
    public const string CollectionName = "crd_coverage_assertions";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoCrdAssertionStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("assertionId"),
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_assertion" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_expiresAt" }),
        ]);
    }

    public async Task SaveAsync(IReadOnlyList<CrdCoverageAssertion> assertions, CancellationToken ct = default)
    {
        if (assertions.Count > 0)
            await _collection.InsertManyAsync(assertions.Select(ToDocument), cancellationToken: ct);
    }

    public async Task<CrdCoverageAssertion?> FindAsync(
        string tenantId, string assertionId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
                     & Builders<BsonDocument>.Filter.Eq("assertionId", assertionId)
                     & Builders<BsonDocument>.Filter.Gt("expiresAt", nowUtc.UtcDateTime);
        var document = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return document is null ? null : FromDocument(document);
    }

    private static BsonDocument ToDocument(CrdCoverageAssertion a) => new()
    {
        ["tenantId"] = a.TenantId,
        ["assertionId"] = a.AssertionId,
        ["coverage"] = a.CoverageReference,
        ["order"] = a.OrderReference is null ? BsonNull.Value : a.OrderReference,
        ["billingCodeSystem"] = a.BillingCodeSystem is null ? BsonNull.Value : a.BillingCodeSystem,
        ["billingCode"] = a.BillingCode is null ? BsonNull.Value : a.BillingCode,
        ["determination"] = a.Determination.ToString(),
        ["questionnaires"] = new BsonArray(a.Questionnaires),
        ["assertedAt"] = a.AssertedAt.UtcDateTime,
        ["expiresAt"] = a.ExpiresAt.UtcDateTime,
    };

    private static CrdCoverageAssertion FromDocument(BsonDocument d) => new()
    {
        TenantId = d["tenantId"].AsString,
        AssertionId = d["assertionId"].AsString,
        CoverageReference = d["coverage"].AsString,
        OrderReference = d["order"].IsBsonNull ? null : d["order"].AsString,
        BillingCodeSystem = d["billingCodeSystem"].IsBsonNull ? null : d["billingCodeSystem"].AsString,
        BillingCode = d["billingCode"].IsBsonNull ? null : d["billingCode"].AsString,
        Determination = Enum.Parse<CrdDetermination>(d["determination"].AsString),
        Questionnaires = d["questionnaires"].AsBsonArray.Select(v => v.AsString).ToList(),
        AssertedAt = new DateTimeOffset(DateTime.SpecifyKind(d["assertedAt"].ToUniversalTime(), DateTimeKind.Utc)),
        ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(d["expiresAt"].ToUniversalTime(), DateTimeKind.Utc)),
    };
}

/// <summary>In-memory store, for tests and for hosts without MongoDB.</summary>
public sealed class InMemoryCrdAssertionStore : ICrdAssertionStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Id), CrdCoverageAssertion> _items = new();

    public Task SaveAsync(IReadOnlyList<CrdCoverageAssertion> assertions, CancellationToken ct = default)
    {
        foreach (var a in assertions)
        {
            if (!_items.TryAdd((a.TenantId, a.AssertionId), a))
                throw new InvalidOperationException($"Duplicate coverage assertion id {a.AssertionId}");
        }
        return Task.CompletedTask;
    }

    public Task<CrdCoverageAssertion?> FindAsync(
        string tenantId, string assertionId, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Task.FromResult(
            _items.TryGetValue((tenantId, assertionId), out var a) && a.ExpiresAt > nowUtc ? a : null);

    /// <summary>Every assertion held, for tests.</summary>
    public IReadOnlyList<CrdCoverageAssertion> All => _items.Values.ToList();
}
