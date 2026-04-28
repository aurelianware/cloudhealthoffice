using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Append-only writer for the
/// <see cref="CredentialingEvent"/> stream (capability 5.6). Mirrors
/// <see cref="IProviderVerificationEventPublisher"/> — single
/// <c>PublishAsync</c> entry point because the
/// <see cref="ICredentialingService"/> constructs the typed payload + sets
/// <see cref="CredentialingEvent.EventId"/> +
/// <see cref="CredentialingEvent.EventType"/>. The publisher's only job is
/// the append-only mechanics: idempotency probe, monotonic version
/// assignment with retry, and cross-tenant <c>_id</c> collision
/// protection.
/// </summary>
public interface ICredentialingEventPublisher
{
    Task<CredentialingEvent> PublishAsync(CredentialingEvent evt, CancellationToken ct = default);
}

public sealed class MongoCredentialingEventPublisher : ICredentialingEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<CredentialingEvent> _collection;
    private readonly ILogger<MongoCredentialingEventPublisher> _logger;

    public MongoCredentialingEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoCredentialingEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:CredentialingEventsContainer"]
            ?? "CredentialingEvents";
        _collection = database.GetCollection<CredentialingEvent>(collectionName);
        _logger = logger;
    }

    public async Task<CredentialingEvent> PublishAsync(CredentialingEvent evt, CancellationToken ct = default)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));
        if (string.IsNullOrEmpty(evt.TenantId)) throw new ArgumentException("TenantId is required.", nameof(evt));
        if (string.IsNullOrEmpty(evt.ProviderId)) throw new ArgumentException("ProviderId is required.", nameof(evt));
        if (string.IsNullOrEmpty(evt.EventId)) throw new ArgumentException("EventId is required.", nameof(evt));

        evt.PartitionKey = CredentialingEvent.BuildPartitionKey(evt.TenantId, evt.ProviderId);
        // Mongo maps Id ⇒ _id which must be unique across the entire
        // collection. EventId is only unique within (TenantId,ProviderId)
        // — two tenants opening the same chain at the same instant would
        // collide on _id alone. Scope _id to PartitionKey:EventId. The
        // (TenantId, ProviderId, EventId) UNIQUE index from the
        // initializer remains the primary idempotency guard.
        evt.Id = $"{evt.PartitionKey}:{evt.EventId}";
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.ProviderId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "CredentialingEvent {EventId} already present for {Tenant}:{Provider} (idempotent no-op)",
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
                    "CredentialingEvent version {Version} conflict for {Tenant}:{Provider}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.ProviderId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append CredentialingEvent for {evt.TenantId}:{evt.ProviderId} after {MaxRetries} attempts");
    }

    private async Task<CredentialingEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct)
    {
        var b = Builders<CredentialingEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId),
            b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string providerId, CancellationToken ct)
    {
        var b = Builders<CredentialingEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Fail-fast fallback used in Cosmos-only deployments without a
/// provisioned credentialing-events stream. Unlike the integrity /
/// version / participation Noop publishers (which are an audit
/// supplement), the credentialing event chain IS the system-of-record
/// for the workflow — if the chain can't be written, the projection
/// patch must NOT proceed (otherwise Provider.CredentialingStatus
/// would mutate without a matching audit record). Throwing
/// <see cref="InvalidOperationException"/> here surfaces 503 from the
/// controllers, matching the publisher-exhaustion contract in the
/// Mongo path.
/// </summary>
public sealed class NoopCredentialingEventPublisher : ICredentialingEventPublisher
{
    private readonly ILogger<NoopCredentialingEventPublisher> _logger;

    public NoopCredentialingEventPublisher(ILogger<NoopCredentialingEventPublisher> logger)
        => _logger = logger;

    public Task<CredentialingEvent> PublishAsync(CredentialingEvent evt, CancellationToken ct = default)
    {
        // Sanitize user-supplied identifier before logging — defense
        // against log injection (CRLF).
        _logger.LogError(
            "CredentialingEventPublisher is not configured; refusing to publish {EventType} event for {ProviderId} " +
            "(the credentialing event chain is the system-of-record).",
            evt?.EventType, Sanitize(evt?.ProviderId));
        throw new InvalidOperationException(
            "CredentialingEventPublisher is not configured. Credentialing workflow endpoints require a " +
            "provisioned events stream — check the Mongo connection or the CosmosDb:CredentialingEventsContainer setting.");
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
