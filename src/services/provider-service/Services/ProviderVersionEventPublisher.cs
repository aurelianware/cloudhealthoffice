using System.Text.Json.Nodes;
using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.Services;

/// <summary>
/// Publishes <see cref="ProviderVersionEvent"/>s to the append-only
/// <c>provider-version-events</c> stream. Mirrors the plan-version
/// pattern: client-supplied <see cref="ProviderVersionEvent.EventId"/>
/// for idempotency, monotonic <see cref="ProviderVersionEvent.Version"/>
/// per <c>(TenantId, ProviderId)</c>.
///
/// Bus fan-out is intentionally not wired here. Downstream publishers
/// (claims-service, coverage-service, etc.) will be added via a
/// decorator that wraps <see cref="IProviderVersionEventPublisher"/>
/// without touching call sites.
/// </summary>
public interface IProviderVersionEventPublisher
{
    Task<ProviderVersionEvent> PublishVersionActivatedAsync(Provider version, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ProviderVersionEvent> PublishVersionSupersededAsync(Provider from, Provider to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ProviderVersionEvent> PublishVersionSuspendedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ProviderVersionEvent> PublishVersionReactivatedAsync(Provider version, Provider? predecessor, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ProviderVersionEvent> PublishVersionTerminatedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
}

public sealed class MongoProviderVersionEventPublisher : IProviderVersionEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<ProviderVersionEvent> _collection;
    private readonly ILogger<MongoProviderVersionEventPublisher> _logger;

    public MongoProviderVersionEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoProviderVersionEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:ProviderVersionEventsContainer"] ?? "ProviderVersionEvents";
        _collection = database.GetCollection<ProviderVersionEvent>(collectionName);
        _logger = logger;
    }

    public Task<ProviderVersionEvent> PublishVersionActivatedAsync(Provider version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["versionNumber"] = version.VersionNumber,
            ["predecessorVersionId"] = version.PredecessorVersionId,
            ["activatedAt"] = version.ActivatedAt
        };

        var evt = new ProviderVersionEvent
        {
            EventId = $"activated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionActivated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ProviderVersionEvent> PublishVersionSupersededAsync(Provider from, Provider to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["fromVersionId"] = from.VersionId,
            ["toVersionId"] = to.VersionId,
            ["reason"] = reason,
            ["supersededAt"] = from.SupersededAt
        };

        var evt = new ProviderVersionEvent
        {
            EventId = $"superseded:{from.VersionId}->{to.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionSuperseded,
            TenantId = from.TenantId,
            ProviderId = from.ProviderId,
            VersionId = from.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ProviderVersionEvent> PublishVersionSuspendedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["reason"] = reason,
            ["suspendedAt"] = version.SuspendedAt
        };

        var evt = new ProviderVersionEvent
        {
            EventId = $"suspended:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionSuspended,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ProviderVersionEvent> PublishVersionReactivatedAsync(Provider version, Provider? predecessor, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["predecessorVersionId"] = predecessor?.VersionId,
            ["activatedAt"] = version.ActivatedAt
        };

        var evt = new ProviderVersionEvent
        {
            EventId = $"reactivated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionReactivated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ProviderVersionEvent> PublishVersionTerminatedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["reason"] = reason,
            ["terminatedAt"] = version.TerminationDate
        };

        var evt = new ProviderVersionEvent
        {
            EventId = $"terminated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionTerminated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    private async Task<ProviderVersionEvent> AppendAsync(ProviderVersionEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = ProviderVersionEvent.BuildPartitionKey(evt.TenantId, evt.ProviderId);
        evt.Id = evt.EventId;
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.ProviderId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "ProviderVersionEvent {EventId} already present for {Tenant}:{Provider} (idempotent no-op)",
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
                    "ProviderVersionEvent version {Version} conflict for {Tenant}:{Provider}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.ProviderId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append ProviderVersionEvent for {evt.TenantId}:{evt.ProviderId} after {MaxRetries} attempts");
    }

    private async Task<ProviderVersionEvent?> GetByEventIdAsync(string tenantId, string providerId, string eventId, CancellationToken ct)
    {
        var b = Builders<ProviderVersionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId), b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string providerId, CancellationToken ct)
    {
        var b = Builders<ProviderVersionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// No-op fallback used when neither Mongo nor a future Cosmos publisher
/// is available (e.g. Cosmos-only deployments without the events stream
/// provisioned yet). Logs a warning so ops can spot the missing wiring.
/// </summary>
public sealed class NoopProviderVersionEventPublisher : IProviderVersionEventPublisher
{
    private readonly ILogger<NoopProviderVersionEventPublisher> _logger;

    public NoopProviderVersionEventPublisher(ILogger<NoopProviderVersionEventPublisher> logger) => _logger = logger;

    public Task<ProviderVersionEvent> PublishVersionActivatedAsync(Provider version, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"activated:{version.VersionId}", ProviderVersionEventType.ProviderVersionActivated, actorId, correlationId);

    public Task<ProviderVersionEvent> PublishVersionSupersededAsync(Provider from, Provider to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(from, $"superseded:{from.VersionId}->{to.VersionId}", ProviderVersionEventType.ProviderVersionSuperseded, actorId, correlationId);

    public Task<ProviderVersionEvent> PublishVersionSuspendedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"suspended:{version.VersionId}", ProviderVersionEventType.ProviderVersionSuspended, actorId, correlationId);

    public Task<ProviderVersionEvent> PublishVersionReactivatedAsync(Provider version, Provider? predecessor, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"reactivated:{version.VersionId}", ProviderVersionEventType.ProviderVersionReactivated, actorId, correlationId);

    public Task<ProviderVersionEvent> PublishVersionTerminatedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"terminated:{version.VersionId}", ProviderVersionEventType.ProviderVersionTerminated, actorId, correlationId);

    private Task<ProviderVersionEvent> DropAndReturn(Provider version, string eventId, ProviderVersionEventType type, string? actorId, string? correlationId)
    {
        _logger.LogWarning(
            "ProviderVersionEventPublisher is not configured; dropping {EventType} for provider {ProviderId} version {VersionId}",
            type, version.ProviderId, version.VersionId);
        return Task.FromResult(new ProviderVersionEvent
        {
            EventId = eventId,
            EventType = type,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });
    }
}
