using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Logging;

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

    /// <summary>Document id: unique per (kind, tenant, claim). Tenant and claim ids are URI-escaped.</summary>
    public static string KeyFor(ClaimReservationKind kind, string tenantId, string claimId)
        => $"{kind.ToString().ToLowerInvariant()}:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(claimId)}";
}

/// <summary>
/// "A claim can be paid once, and reversed once." Before a run creates the
/// payment (or reversal payment) for a claim it reserves the claim here; only
/// one run ever holds a given (tenant, kind, claim). Chosen over a unique index
/// on <c>Payments.ClaimPayments.ClaimId</c> because a Mongo unique multikey
/// index cannot be scoped to non-reversal payments for documents written before
/// <c>IsReversal</c> existed. Reservations are never deleted once their payment
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
                LogSanitizer.Sanitize(reservation.ClaimId), reservation.Kind, LogSanitizer.Sanitize(reservation.RunId));
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
}
