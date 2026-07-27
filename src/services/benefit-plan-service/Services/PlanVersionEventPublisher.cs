using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.Services;

/// <summary>
/// Publishes <see cref="PlanVersionEvent"/>s to the append-only
/// <c>plan-version-events</c> stream. Mirrors the member-events pattern:
/// client-supplied <see cref="PlanVersionEvent.EventId"/> for idempotency,
/// monotonic <see cref="PlanVersionEvent.Version"/> per <c>(TenantId, PlanId)</c>.
///
/// Bus fan-out is intentionally not wired here. Downstream publishers
/// (claims-service, eligibility-service, etc.) will be added via a
/// decorator that wraps <see cref="IPlanVersionEventPublisher"/> without
/// touching call sites.
/// </summary>
public interface IPlanVersionEventPublisher
{
    Task<PlanVersionEvent> PublishVersionPublishedAsync(BenefitPlan version, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<PlanVersionEvent> PublishVersionSupersededAsync(BenefitPlan from, BenefitPlan to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);

    /// <summary>Published version moved to Superseded with no successor -- the plan ends.</summary>
    Task<PlanVersionEvent> PublishVersionTerminatedAsync(BenefitPlan version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
}

public sealed class MongoPlanVersionEventPublisher : IPlanVersionEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<PlanVersionEvent> _collection;
    private readonly ILogger<MongoPlanVersionEventPublisher> _logger;

    public MongoPlanVersionEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoPlanVersionEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:PlanVersionEventsContainer"] ?? "PlanVersionEvents";
        _collection = database.GetCollection<PlanVersionEvent>(collectionName);
        _logger = logger;
    }

    public Task<PlanVersionEvent> PublishVersionPublishedAsync(BenefitPlan version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["versionNumber"] = version.VersionNumber,
            ["predecessorVersionId"] = version.PredecessorVersionId,
            ["effectiveDate"] = version.EffectiveDate,
            ["publishedAt"] = version.PublishedAt
        };

        var evt = new PlanVersionEvent
        {
            EventId = $"published:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionPublished,
            TenantId = version.TenantId,
            PlanId = version.PlanId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<PlanVersionEvent> PublishVersionSupersededAsync(BenefitPlan from, BenefitPlan to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["fromVersionId"] = from.VersionId,
            ["toVersionId"] = to.VersionId,
            ["reason"] = reason,
            ["supersededAt"] = from.SupersededAt
        };

        var evt = new PlanVersionEvent
        {
            EventId = $"superseded:{from.VersionId}->{to.VersionId}",
            EventType = PlanVersionEventType.PlanVersionSuperseded,
            TenantId = from.TenantId,
            PlanId = from.PlanId,
            VersionId = from.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<PlanVersionEvent> PublishVersionTerminatedAsync(BenefitPlan version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.VersionId,
            ["reason"] = reason,
            ["terminatedAt"] = version.SupersededAt
        };

        var evt = new PlanVersionEvent
        {
            EventId = $"terminated:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionTerminated,
            TenantId = version.TenantId,
            PlanId = version.PlanId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    private async Task<PlanVersionEvent> AppendAsync(PlanVersionEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = PlanVersionEvent.BuildPartitionKey(evt.TenantId, evt.PlanId);
        evt.Id = evt.EventId;
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.PlanId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "PlanVersionEvent {EventId} already present for {Tenant}:{Plan} (idempotent no-op)",
                Sanitize(evt.EventId), Sanitize(evt.TenantId), Sanitize(evt.PlanId));
            return existing;
        }

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            evt.Version = await GetNextVersionAsync(evt.TenantId, evt.PlanId, ct);
            try
            {
                await _collection.InsertOneAsync(evt, cancellationToken: ct);
                return evt;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var refetch = await GetByEventIdAsync(evt.TenantId, evt.PlanId, evt.EventId, ct);
                if (refetch != null) return refetch;

                _logger.LogWarning(
                    "PlanVersionEvent version {Version} conflict for {Tenant}:{Plan}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.PlanId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append PlanVersionEvent for {evt.TenantId}:{evt.PlanId} after {MaxRetries} attempts");
    }

    private async Task<PlanVersionEvent?> GetByEventIdAsync(string tenantId, string planId, string eventId, CancellationToken ct)
    {
        var b = Builders<PlanVersionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.PlanId, planId), b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string planId, CancellationToken ct)
    {
        var b = Builders<PlanVersionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.PlanId, planId));
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
public sealed class NoopPlanVersionEventPublisher : IPlanVersionEventPublisher
{
    private readonly ILogger<NoopPlanVersionEventPublisher> _logger;

    public NoopPlanVersionEventPublisher(ILogger<NoopPlanVersionEventPublisher> logger) => _logger = logger;

    public Task<PlanVersionEvent> PublishVersionPublishedAsync(BenefitPlan version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "PlanVersionEventPublisher is not configured; dropping PlanVersionPublished for plan {PlanId} version {VersionId}",
            version.PlanId, version.VersionId);
        return Task.FromResult(new PlanVersionEvent
        {
            EventId = $"published:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionPublished,
            TenantId = version.TenantId,
            PlanId = version.PlanId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });
    }

    public Task<PlanVersionEvent> PublishVersionSupersededAsync(BenefitPlan from, BenefitPlan to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "PlanVersionEventPublisher is not configured; dropping PlanVersionSuperseded for plan {PlanId} {From} -> {To}",
            from.PlanId, from.VersionId, to.VersionId);
        return Task.FromResult(new PlanVersionEvent
        {
            EventId = $"superseded:{from.VersionId}->{to.VersionId}",
            EventType = PlanVersionEventType.PlanVersionSuperseded,
            TenantId = from.TenantId,
            PlanId = from.PlanId,
            VersionId = from.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });
    }

    public Task<PlanVersionEvent> PublishVersionTerminatedAsync(BenefitPlan version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "PlanVersionEventPublisher is not configured; dropping PlanVersionTerminated for plan {PlanId} version {VersionId}",
            version.PlanId, version.VersionId);
        return Task.FromResult(new PlanVersionEvent
        {
            EventId = $"terminated:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionTerminated,
            TenantId = version.TenantId,
            PlanId = version.PlanId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });
    }
}
