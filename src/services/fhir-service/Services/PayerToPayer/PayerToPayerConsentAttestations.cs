using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// A requesting payer's Payer-to-Payer consent for one member, accepted by
/// <c>$member-match</c> or <c>$member-data-export</c> (ADR 022). It binds the
/// member's opt-in to the payer that collected it: an export is served only to a
/// payer with an attestation in force for that member. Holds no consent content,
/// only its fingerprint and the facts the decision needs.
/// </summary>
public sealed record PayerToPayerConsentAttestation
{
    public required string TenantId { get; init; }
    public required string MemberId { get; init; }

    /// <summary>The requesting payer, as the authenticated client is registered.</summary>
    public required string ReceivingPayerId { get; init; }

    public required string Policy { get; init; }
    public required DateTime PeriodStartUtc { get; init; }

    /// <summary>Null when the consent gave no end.</summary>
    public DateTime? PeriodEndUtc { get; init; }

    public required string ConsentFingerprint { get; init; }
    public required DateTime RecordedAtUtc { get; init; }
    public required string RecordedBy { get; init; }

    /// <summary>Opaque id for audit: the fingerprint names the evidence.</summary>
    public string Id => $"p2p-consent:{ConsentFingerprint}";

    public bool InForce(DateTime nowUtc) =>
        nowUtc >= PeriodStartUtc && (PeriodEndUtc is not { } end || nowUtc < end);
}

/// <summary>Durable store of accepted Payer-to-Payer consents, one current record per (tenant, member, payer).</summary>
public interface IPayerToPayerConsentAttestationStore
{
    /// <summary>Records the consent, replacing the payer's earlier one for the member.</summary>
    Task RecordAsync(PayerToPayerConsentAttestation attestation, CancellationToken ct = default);

    /// <summary>The payer's current consent for the member when it is in force at <paramref name="nowUtc"/>.</summary>
    Task<PayerToPayerConsentAttestation?> FindInForceAsync(
        string tenantId, string memberId, string receivingPayerId, DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>
/// MongoDB store (<c>p2p_consent_attestations</c>), unique on (tenantId, memberId,
/// receivingPayerId). Singleton: indexes are ensured once at construction.
/// </summary>
public sealed class MongoPayerToPayerConsentAttestationStore : IPayerToPayerConsentAttestationStore
{
    public const string CollectionName = "p2p_consent_attestations";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoPayerToPayerConsentAttestationStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("memberId").Ascending("receivingPayerId"),
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_payer" }));
    }

    public async Task RecordAsync(PayerToPayerConsentAttestation a, CancellationToken ct = default)
    {
        var filter = Key(a.TenantId, a.MemberId, a.ReceivingPayerId);
        var document = new BsonDocument
        {
            ["tenantId"] = a.TenantId,
            ["memberId"] = a.MemberId,
            ["receivingPayerId"] = a.ReceivingPayerId,
            ["policy"] = a.Policy,
            ["periodStart"] = a.PeriodStartUtc,
            ["periodEnd"] = a.PeriodEndUtc is { } end ? end : BsonNull.Value,
            ["consentFingerprint"] = a.ConsentFingerprint,
            ["recordedAt"] = a.RecordedAtUtc,
            ["recordedBy"] = a.RecordedBy,
        };
        try
        {
            await _collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two upserts raced to insert the same key; the document now exists,
            // so the retry replaces it.
            await _collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, ct);
        }
    }

    public async Task<PayerToPayerConsentAttestation?> FindInForceAsync(
        string tenantId, string memberId, string receivingPayerId, DateTime nowUtc, CancellationToken ct = default)
    {
        var d = await _collection.Find(Key(tenantId, memberId, receivingPayerId)).FirstOrDefaultAsync(ct);
        if (d is null) return null;
        var attestation = new PayerToPayerConsentAttestation
        {
            TenantId = d["tenantId"].AsString,
            MemberId = d["memberId"].AsString,
            ReceivingPayerId = d["receivingPayerId"].AsString,
            Policy = d["policy"].AsString,
            PeriodStartUtc = d["periodStart"].ToUniversalTime(),
            PeriodEndUtc = d["periodEnd"].IsBsonNull ? null : d["periodEnd"].ToUniversalTime(),
            ConsentFingerprint = d["consentFingerprint"].AsString,
            RecordedAtUtc = d["recordedAt"].ToUniversalTime(),
            RecordedBy = d["recordedBy"].AsString,
        };
        return attestation.InForce(nowUtc) ? attestation : null;
    }

    private static FilterDefinition<BsonDocument> Key(string tenantId, string memberId, string payerId) =>
        Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
        & Builders<BsonDocument>.Filter.Eq("memberId", memberId)
        & Builders<BsonDocument>.Filter.Eq("receivingPayerId", payerId);
}

/// <summary>In-process store for tests and local runs; not durable.</summary>
public sealed class InMemoryPayerToPayerConsentAttestationStore : IPayerToPayerConsentAttestationStore
{
    private readonly ConcurrentDictionary<(string, string, string), PayerToPayerConsentAttestation> _records = new();

    public IReadOnlyCollection<PayerToPayerConsentAttestation> Records => _records.Values.ToList();

    public Task RecordAsync(PayerToPayerConsentAttestation a, CancellationToken ct = default)
    {
        _records[(a.TenantId, a.MemberId, a.ReceivingPayerId)] = a;
        return Task.CompletedTask;
    }

    public Task<PayerToPayerConsentAttestation?> FindInForceAsync(
        string tenantId, string memberId, string receivingPayerId, DateTime nowUtc, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue((tenantId, memberId, receivingPayerId), out var a) && a.InForce(nowUtc) ? a : null);
}
