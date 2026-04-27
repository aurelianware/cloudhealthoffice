using System.Text.Json.Nodes;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Publishes <see cref="NetworkParticipationEvent"/>s to the append-only
/// <c>ProviderParticipationEvents</c> stream. Mirrors
/// <see cref="IProviderVerificationEventPublisher"/>: client-supplied
/// <see cref="NetworkParticipationEvent.EventId"/> for idempotency,
/// monotonic <see cref="NetworkParticipationEvent.Version"/> per
/// <c>(TenantId, ProviderId)</c>.
///
/// <para>
/// Capability 5.5 ships the producer; no cross-service consumer is
/// wired. The audit value is primary — regulators and incident
/// responders need a record of when each participation's panel-gating
/// was set, regardless of whether a downstream consumer subscribes
/// today.
/// </para>
/// </summary>
public interface INetworkParticipationEventPublisher
{
    Task<NetworkParticipationEvent> PublishPanelGatingBackfilledAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        string? planId,
        string? networkId,
        LineOfBusiness lineOfBusiness,
        string backfillRunId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

public sealed class MongoNetworkParticipationEventPublisher : INetworkParticipationEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<NetworkParticipationEvent> _collection;
    private readonly ILogger<MongoNetworkParticipationEventPublisher> _logger;

    public MongoNetworkParticipationEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoNetworkParticipationEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:ProviderParticipationEventsContainer"]
            ?? "ProviderParticipationEvents";
        _collection = database.GetCollection<NetworkParticipationEvent>(collectionName);
        _logger = logger;
    }

    public Task<NetworkParticipationEvent> PublishPanelGatingBackfilledAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        string? planId,
        string? networkId,
        LineOfBusiness lineOfBusiness,
        string backfillRunId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["participationIndex"] = participationIndex,
            ["planId"] = planId,
            ["networkId"] = networkId,
            ["lineOfBusiness"] = lineOfBusiness.ToString(),
            ["backfillRunId"] = backfillRunId,
        };

        var evt = new NetworkParticipationEvent
        {
            EventId = NetworkParticipationEvent.BuildBackfilledEventId(
                providerId, participationIndex, backfillRunId),
            EventType = NetworkParticipationEventType.PanelGatingBackfilled,
            TenantId = tenantId,
            ProviderId = providerId,
            ParticipationIndex = participationIndex,
            PlanId = planId,
            NetworkId = networkId,
            LineOfBusiness = lineOfBusiness,
            BackfillRunId = backfillRunId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload,
        };
        return AppendAsync(evt, ct);
    }

    private async Task<NetworkParticipationEvent> AppendAsync(
        NetworkParticipationEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = NetworkParticipationEvent.BuildPartitionKey(
            evt.TenantId, evt.ProviderId);
        // Mongo maps Id ⇒ _id which must be unique across the entire
        // collection. EventId is only unique within (TenantId,ProviderId)
        // — two tenants backfilling the same providerId at the same
        // index in the same run would collide on _id alone. Scope _id
        // to PartitionKey:EventId. The (TenantId, ProviderId, EventId)
        // UNIQUE index from the initializer remains in place as the
        // primary idempotency guard. This mirrors the lesson learned
        // in 5.4.5's MongoProviderVerificationEventPublisher.
        evt.Id = $"{evt.PartitionKey}:{evt.EventId}";
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.ProviderId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "NetworkParticipationEvent {EventId} already present for {Tenant}:{Provider} (idempotent no-op)",
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
                    "NetworkParticipationEvent version {Version} conflict for {Tenant}:{Provider}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.ProviderId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append NetworkParticipationEvent for {evt.TenantId}:{evt.ProviderId} after {MaxRetries} attempts");
    }

    private async Task<NetworkParticipationEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct)
    {
        var b = Builders<NetworkParticipationEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderId, providerId),
            b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string providerId, CancellationToken ct)
    {
        var b = Builders<NetworkParticipationEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// No-op fallback used when no Mongo publisher is available (Cosmos-only
/// deployments without the participation-events stream provisioned).
/// Logs a warning so ops can spot the missing wiring.
/// </summary>
public sealed class NoopNetworkParticipationEventPublisher : INetworkParticipationEventPublisher
{
    private readonly ILogger<NoopNetworkParticipationEventPublisher> _logger;

    public NoopNetworkParticipationEventPublisher(ILogger<NoopNetworkParticipationEventPublisher> logger)
        => _logger = logger;

    public Task<NetworkParticipationEvent> PublishPanelGatingBackfilledAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        string? planId,
        string? networkId,
        LineOfBusiness lineOfBusiness,
        string backfillRunId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        _logger.LogWarning(
            "NetworkParticipationEventPublisher is not configured; dropping panel-gating-backfilled event for {ProviderId}:{Index}",
            providerId, participationIndex);
        return Task.FromResult(new NetworkParticipationEvent
        {
            EventId = NetworkParticipationEvent.BuildBackfilledEventId(
                providerId, participationIndex, backfillRunId),
            EventType = NetworkParticipationEventType.PanelGatingBackfilled,
            TenantId = tenantId,
            ProviderId = providerId,
            ParticipationIndex = participationIndex,
            PlanId = planId,
            NetworkId = networkId,
            LineOfBusiness = lineOfBusiness,
            BackfillRunId = backfillRunId,
            ActorId = actorId,
            CorrelationId = correlationId,
        });
    }
}
