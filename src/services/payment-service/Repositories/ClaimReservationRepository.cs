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
/// exists.
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
}
