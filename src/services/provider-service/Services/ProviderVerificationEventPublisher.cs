using System.Text.Json.Nodes;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Publishes <see cref="ProviderVerificationEvent"/>s to the append-only
/// <c>ProviderVerificationEvents</c> stream. Mirrors
/// <see cref="IProviderVersionEventPublisher"/>: client-supplied
/// <see cref="ProviderVerificationEvent.EventId"/> for idempotency,
/// monotonic <see cref="ProviderVerificationEvent.Version"/> per
/// <c>(TenantId, ProviderId)</c>.
///
/// <para>
/// Capability 5.4.5 ships the producer; no cross-service consumer is
/// wired. The decorator path used for
/// <see cref="IProviderVersionEventPublisher"/> applies here too if a
/// future capability needs bus fan-out.
/// </para>
/// </summary>
public interface IProviderVerificationEventPublisher
{
    Task<ProviderVerificationEvent> PublishRefreshedAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset verifiedAt,
        DateTimeOffset? nextVerificationDue,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

public sealed class MongoProviderVerificationEventPublisher : IProviderVerificationEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<ProviderVerificationEvent> _collection;
    private readonly ILogger<MongoProviderVerificationEventPublisher> _logger;

    public MongoProviderVerificationEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoProviderVerificationEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:ProviderVerificationEventsContainer"]
            ?? "ProviderVerificationEvents";
        _collection = database.GetCollection<ProviderVerificationEvent>(collectionName);
        _logger = logger;
    }

    public Task<ProviderVerificationEvent> PublishRefreshedAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset verifiedAt,
        DateTimeOffset? nextVerificationDue,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["integrityScore"] = integrityScore,
            ["integrityRating"] = integrityRating,
            ["verifiedAt"] = verifiedAt,
            ["nextVerificationDue"] = nextVerificationDue,
        };

        var evt = new ProviderVerificationEvent
        {
            EventId = ProviderVerificationEvent.BuildRefreshedEventId(providerId, verifiedAt),
            EventType = ProviderVerificationEventType.ProviderVerificationRefreshed,
            TenantId = tenantId,
            ProviderId = providerId,
            IntegrityScore = integrityScore,
            IntegrityRating = integrityRating,
            VerifiedAt = verifiedAt,
            NextVerificationDue = nextVerificationDue,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload,
        };
        return AppendAsync(evt, ct);
    }

    private async Task<ProviderVerificationEvent> AppendAsync(
        ProviderVerificationEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = ProviderVerificationEvent.BuildPartitionKey(evt.TenantId, evt.ProviderId);
        // Mongo maps Id ⇒ _id which must be unique across the entire
        // collection. EventId is only unique within (TenantId,ProviderId)
        // — two tenants verifying the same NPI at the same instant
        // would collide on _id alone. Scope _id to PartitionKey:EventId.
        // The (TenantId, ProviderId, EventId) UNIQUE index from the
        // initializer remains in place as the primary idempotency guard.
        evt.Id = $"{evt.PartitionKey}:{evt.EventId}";
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.ProviderId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "ProviderVerificationEvent {EventId} already present for {Tenant}:{Provider} (idempotent no-op)",
                Sanitize(evt.EventId), Sanitize(evt.TenantId), Sanitize(evt.ProviderId));
            return existing;
        }

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            evt.Version = await GetNextVersionAsync(evt.TenantId, evt.ProviderId, ct);
            try
            {
                await _collection.InsertOneAsync(evt, cancellationToken: ct);
                return evt;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var refetch = await GetByEventIdAsync(evt.TenantId, evt.ProviderId, evt.EventId, ct);
                if (refetch != null) return refetch;

                _logger.LogWarning(
                    "ProviderVerificationEvent version {Version} conflict for {Tenant}:{Provider}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.ProviderId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append ProviderVerificationEvent for {evt.TenantId}:{evt.ProviderId} after {MaxRetries} attempts");
    }

    private async Task<ProviderVerificationEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct)
    {
        var b = Builders<ProviderVerificationEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId),
            b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string providerId, CancellationToken ct)
    {
        var b = Builders<ProviderVerificationEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// No-op fallback used when no Mongo publisher is available (e.g.
/// Cosmos-only deployments without the verification-events stream
/// provisioned). Logs a warning so ops can spot the missing wiring.
/// </summary>
public sealed class NoopProviderVerificationEventPublisher : IProviderVerificationEventPublisher
{
    private readonly ILogger<NoopProviderVerificationEventPublisher> _logger;

    public NoopProviderVerificationEventPublisher(ILogger<NoopProviderVerificationEventPublisher> logger)
        => _logger = logger;

    public Task<ProviderVerificationEvent> PublishRefreshedAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset verifiedAt,
        DateTimeOffset? nextVerificationDue,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        _logger.LogWarning(
            "ProviderVerificationEventPublisher is not configured; dropping refresh event for {ProviderId}",
            providerId);
        return Task.FromResult(new ProviderVerificationEvent
        {
            EventId = ProviderVerificationEvent.BuildRefreshedEventId(providerId, verifiedAt),
            EventType = ProviderVerificationEventType.ProviderVerificationRefreshed,
            TenantId = tenantId,
            ProviderId = providerId,
            IntegrityScore = integrityScore,
            IntegrityRating = integrityRating,
            VerifiedAt = verifiedAt,
            NextVerificationDue = nextVerificationDue,
            ActorId = actorId,
            CorrelationId = correlationId,
        });
    }
}
