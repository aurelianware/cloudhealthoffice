using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace PaymentService.Repositories;

/// <summary>What a reservation guards: paying a claim, or reversing (recouping) it.</summary>
public enum ClaimReservationKind
{
    Payment,
    Reversal,
}

/// <summary>
/// One claim held by one run, for one kind. The document id is derived from
/// (kind, tenant, claim), so the store's own primary-key uniqueness makes the
/// insert an atomic insert-if-absent: a second run that tries to reserve the
/// same claim fails, however close together the two runs are.
/// </summary>
public sealed class ClaimReservation
{
    [BsonId]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public ClaimReservationKind Kind { get; set; }

    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("runNumber")]
    public string? RunNumber { get; set; }

    [JsonPropertyName("reservedBy")]
    public string? ReservedBy { get; set; }

    [JsonPropertyName("reservedAt")]
    public DateTime ReservedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set once when the reservation is inserted. A release (automatic or
    /// manual) deletes the reservation only while it still carries the version
    /// it classified, so it can never delete a reservation another run took
    /// after an earlier release. Null on reservations written before versions.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Reconciliation could not release it safely; a person must look (see <see cref="AttentionReason"/>).</summary>
    [JsonPropertyName("needsAttention")]
    public bool NeedsAttention { get; set; }

    [JsonPropertyName("attentionReason")]
    public string? AttentionReason { get; set; }

    [JsonPropertyName("flaggedAt")]
    public DateTime? FlaggedAt { get; set; }

    /// <summary>Document id: unique per (kind, tenant, claim). Characters Cosmos forbids in ids are escaped.</summary>
    public static string KeyFor(ClaimReservationKind kind, string tenantId, string claimId)
        => $"{kind.ToString().ToLowerInvariant()}:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(claimId)}";
}

/// <summary>
/// "A claim can be paid once, and reversed once." Before a run creates the
/// payment (or reversal payment) for a claim it reserves the claim here; only
/// one run ever holds a given (tenant, kind, claim). Chosen over a unique index
/// on <c>Payments.ClaimPayments.ClaimId</c> because it works the same on both
/// stores: a Mongo unique multikey index cannot be scoped to non-reversal
/// payments for documents written before <c>IsReversal</c> existed, and a Cosmos
/// unique key policy can only be set when a container is created and cannot
/// cover array elements. Reservations are never deleted once their payment
/// exists. A reservation left by a run that failed or was cancelled before
/// paying is released by <c>ReservationReconciliationService</c> (automatically
/// when payment-service holds no payment and no 835 for the claim; otherwise by
/// a second approver).
/// </summary>
public interface IClaimReservationRepository
{
    /// <summary>True when the run now holds the claim; false when another run (or this one) already does.</summary>
    Task<bool> TryReserveAsync(ClaimReservation reservation);

    /// <summary>
    /// Releases a reservation this run holds for a claim it never attempted to
    /// pay (the run failed first). Never called once a payment insert was tried.
    /// </summary>
    Task ReleaseAsync(ClaimReservationKind kind, string tenantId, string claimId, string runId);

    /// <summary>The reservation of a claim in a tenant, or null.</summary>
    Task<ClaimReservation?> GetAsync(ClaimReservationKind kind, string tenantId, string claimId);

    /// <summary>Every tenant that holds at least one reservation (the reconciliation job works per tenant).</summary>
    Task<IReadOnlyCollection<string>> ListTenantsAsync();

    /// <summary>One tenant's reservations; only those flagged NeedsAttention when <paramref name="needsAttentionOnly"/>.</summary>
    Task<IReadOnlyList<ClaimReservation>> ListByTenantAsync(string tenantId, bool needsAttentionOnly = false);

    /// <summary>
    /// Deletes the reservation only while it is the one observed: same id, same
    /// run, same <see cref="ClaimReservation.Version"/>. False when it is gone or
    /// changed hands (another release and a new run's reservation came between).
    /// </summary>
    Task<bool> TryDeleteIfUnchangedAsync(ClaimReservation observed);

    /// <summary>
    /// Flags (reason not null) or clears (reason null) NeedsAttention, only while
    /// the reservation is the one observed. False when it is gone or changed hands.
    /// </summary>
    Task<bool> TrySetAttentionIfUnchangedAsync(ClaimReservation observed, string? reason, DateTime? flaggedAt);
}

public sealed class ClaimReservationRepositoryMongo : IClaimReservationRepository
{
    public const string CollectionName = "PaymentClaimReservations";

    private readonly IMongoCollection<ClaimReservation> _collection;
    private readonly ILogger<ClaimReservationRepositoryMongo> _logger;

    public ClaimReservationRepositoryMongo(IMongoDatabase database, ILogger<ClaimReservationRepositoryMongo> logger)
    {
        _collection = database.GetCollection<ClaimReservation>(CollectionName);
        _logger = logger;
    }

    public async Task<bool> TryReserveAsync(ClaimReservation reservation)
    {
        reservation.Id = ClaimReservation.KeyFor(reservation.Kind, reservation.TenantId, reservation.ClaimId);
        reservation.Version ??= Guid.NewGuid().ToString("N");
        try
        {
            await _collection.InsertOneAsync(reservation);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            _logger.LogWarning("Claim {ClaimId} already reserved for {Kind}; run {RunId} skips it",
                Sanitize(reservation.ClaimId), reservation.Kind, Sanitize(reservation.RunId));
            return false;
        }
    }

    public Task ReleaseAsync(ClaimReservationKind kind, string tenantId, string claimId, string runId)
    {
        var filter = Builders<ClaimReservation>.Filter.And(
            Builders<ClaimReservation>.Filter.Eq(x => x.Id, ClaimReservation.KeyFor(kind, tenantId, claimId)),
            Builders<ClaimReservation>.Filter.Eq(x => x.RunId, runId));
        return _collection.DeleteOneAsync(filter);
    }

    public async Task<ClaimReservation?> GetAsync(ClaimReservationKind kind, string tenantId, string claimId)
    {
        var filter = Builders<ClaimReservation>.Filter.And(
            Builders<ClaimReservation>.Filter.Eq(x => x.Id, ClaimReservation.KeyFor(kind, tenantId, claimId)),
            Builders<ClaimReservation>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyCollection<string>> ListTenantsAsync()
    {
        var cursor = await _collection.DistinctAsync(x => x.TenantId, Builders<ClaimReservation>.Filter.Empty);
        return (await cursor.ToListAsync()).Where(t => !string.IsNullOrEmpty(t)).ToList();
    }

    public async Task<IReadOnlyList<ClaimReservation>> ListByTenantAsync(string tenantId, bool needsAttentionOnly = false)
    {
        var filter = Builders<ClaimReservation>.Filter.Eq(x => x.TenantId, tenantId);
        if (needsAttentionOnly)
            filter &= Builders<ClaimReservation>.Filter.Eq(x => x.NeedsAttention, true);
        return await _collection.Find(filter).SortBy(x => x.ReservedAt).ToListAsync();
    }

    public async Task<bool> TryDeleteIfUnchangedAsync(ClaimReservation observed)
    {
        var result = await _collection.DeleteOneAsync(Unchanged(observed));
        return result.DeletedCount == 1;
    }

    public async Task<bool> TrySetAttentionIfUnchangedAsync(ClaimReservation observed, string? reason, DateTime? flaggedAt)
    {
        var update = Builders<ClaimReservation>.Update
            .Set(x => x.NeedsAttention, reason != null)
            .Set(x => x.AttentionReason, reason)
            .Set(x => x.FlaggedAt, reason != null ? flaggedAt : null);
        var result = await _collection.UpdateOneAsync(Unchanged(observed), update);
        return result.MatchedCount == 1;
    }

    /// <summary>Same document, same run, same version (Eq null also matches a missing field).</summary>
    private static FilterDefinition<ClaimReservation> Unchanged(ClaimReservation observed)
        => Builders<ClaimReservation>.Filter.And(
            Builders<ClaimReservation>.Filter.Eq(x => x.Id, observed.Id),
            Builders<ClaimReservation>.Filter.Eq(x => x.TenantId, observed.TenantId),
            Builders<ClaimReservation>.Filter.Eq(x => x.RunId, observed.RunId),
            Builders<ClaimReservation>.Filter.Eq(x => x.Version, observed.Version));

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Cosmos: container <c>PaymentClaimReservations</c>, partition key
/// <c>/tenantId</c>, created if absent. Item ids are unique within a logical
/// partition, so <c>CreateItemAsync</c> answering 409 Conflict is the
/// insert-if-absent; no unique key policy (or container rebuild) is needed.
/// </summary>
public sealed class ClaimReservationRepositoryCosmos : IClaimReservationRepository
{
    public const string ContainerName = "PaymentClaimReservations";

    private readonly Lazy<Task<Container>> _container;
    private readonly ILogger<ClaimReservationRepositoryCosmos> _logger;

    public ClaimReservationRepositoryCosmos(
        CosmosClient client, IConfiguration configuration, ILogger<ClaimReservationRepositoryCosmos> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = new Lazy<Task<Container>>(async () =>
        {
            var response = await client.GetDatabase(databaseName)
                .CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/tenantId"));
            return response.Container;
        });
        _logger = logger;
    }

    public async Task<bool> TryReserveAsync(ClaimReservation reservation)
    {
        reservation.Id = ClaimReservation.KeyFor(reservation.Kind, reservation.TenantId, reservation.ClaimId);
        reservation.Version ??= Guid.NewGuid().ToString("N");
        var container = await _container.Value;
        try
        {
            await container.CreateItemAsync(reservation, new PartitionKey(reservation.TenantId));
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Claim {ClaimId} already reserved for {Kind}; run {RunId} skips it",
                reservation.ClaimId.Replace("\r", "").Replace("\n", ""), reservation.Kind,
                reservation.RunId.Replace("\r", "").Replace("\n", ""));
            return false;
        }
    }

    public async Task ReleaseAsync(ClaimReservationKind kind, string tenantId, string claimId, string runId)
    {
        var container = await _container.Value;
        var id = ClaimReservation.KeyFor(kind, tenantId, claimId);
        try
        {
            var current = await container.ReadItemAsync<ClaimReservation>(id, new PartitionKey(tenantId));
            if (current.Resource.RunId != runId)
                return;
            await container.DeleteItemAsync<ClaimReservation>(id, new PartitionKey(tenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag });
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // Gone already, or changed hands: nothing of ours to release.
        }
    }

    public async Task<ClaimReservation?> GetAsync(ClaimReservationKind kind, string tenantId, string claimId)
    {
        var container = await _container.Value;
        try
        {
            var response = await container.ReadItemAsync<ClaimReservation>(
                ClaimReservation.KeyFor(kind, tenantId, claimId), new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyCollection<string>> ListTenantsAsync()
    {
        var container = await _container.Value;
        var found = new HashSet<string>(StringComparer.Ordinal);
        var iterator = container.GetItemQueryIterator<string>(new QueryDefinition("SELECT DISTINCT VALUE c.tenantId FROM c"));
        while (iterator.HasMoreResults)
            found.UnionWith((await iterator.ReadNextAsync()).Where(t => !string.IsNullOrEmpty(t)));
        return found;
    }

    public async Task<IReadOnlyList<ClaimReservation>> ListByTenantAsync(string tenantId, bool needsAttentionOnly = false)
    {
        var container = await _container.Value;
        var query = new QueryDefinition(needsAttentionOnly
                ? "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.needsAttention = true ORDER BY c.reservedAt"
                : "SELECT * FROM c WHERE c.tenantId = @tenantId ORDER BY c.reservedAt")
            .WithParameter("@tenantId", tenantId);
        var results = new List<ClaimReservation>();
        var iterator = container.GetItemQueryIterator<ClaimReservation>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    public async Task<bool> TryDeleteIfUnchangedAsync(ClaimReservation observed)
    {
        var container = await _container.Value;
        try
        {
            var current = await container.ReadItemAsync<ClaimReservation>(observed.Id, new PartitionKey(observed.TenantId));
            if (!IsSame(current.Resource, observed))
                return false;
            // If-Match pins the delete to the version just compared.
            await container.DeleteItemAsync<ClaimReservation>(observed.Id, new PartitionKey(observed.TenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> TrySetAttentionIfUnchangedAsync(ClaimReservation observed, string? reason, DateTime? flaggedAt)
    {
        var container = await _container.Value;
        try
        {
            var current = await container.ReadItemAsync<ClaimReservation>(observed.Id, new PartitionKey(observed.TenantId));
            if (!IsSame(current.Resource, observed))
                return false;
            var updated = current.Resource;
            updated.NeedsAttention = reason != null;
            updated.AttentionReason = reason;
            updated.FlaggedAt = reason != null ? flaggedAt : null;
            await container.ReplaceItemAsync(updated, updated.Id, new PartitionKey(updated.TenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    private static bool IsSame(ClaimReservation current, ClaimReservation observed)
        => current.RunId == observed.RunId && current.Version == observed.Version;
}
