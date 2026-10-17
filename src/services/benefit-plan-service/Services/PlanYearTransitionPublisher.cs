using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Logging;

namespace BenefitPlanService.Services;

/// <summary>
/// Publishes <see cref="PlanYearTransitionEvent"/>s to the append-only
/// <c>plan-year-transition-events</c> stream. Mirrors
/// <see cref="MongoPlanVersionEventPublisher"/>: deterministic
/// <see cref="PlanYearTransitionEvent.EventId"/> for idempotency,
/// monotonic <see cref="PlanYearTransitionEvent.Version"/> per
/// <c>(TenantId, PlanId)</c>.
///
/// <para>
/// Bus fan-out is intentionally not wired here. accumulator-service
/// reads from the Mongo stream today; a Service Bus decorator can layer
/// on without touching call sites — see Phase 3.
/// </para>
/// </summary>
public interface IPlanYearTransitionPublisher
{
    Task<PlanYearTransitionEvent> PublishApproachingAsync(
        BenefitPlan plan,
        DateTime planYearEnd,
        DateTime nextPlanYearStart,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);

    Task<PlanYearTransitionEvent> PublishTransitionAsync(
        BenefitPlan plan,
        DateTime planYearEnd,
        DateTime nextPlanYearStart,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

public sealed class MongoPlanYearTransitionPublisher : IPlanYearTransitionPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<PlanYearTransitionEvent> _collection;
    private readonly ILogger<MongoPlanYearTransitionPublisher> _logger;

    public MongoPlanYearTransitionPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoPlanYearTransitionPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:PlanYearTransitionEventsContainer"]
            ?? "PlanYearTransitionEvents";
        _collection = database.GetCollection<PlanYearTransitionEvent>(collectionName);
        _logger = logger;
    }

    public Task<PlanYearTransitionEvent> PublishApproachingAsync(
        BenefitPlan plan, DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId, CancellationToken ct = default)
        => AppendAsync(Build(plan, PlanYearTransitionType.ApproachingTransition,
            planYearEnd, nextPlanYearStart, actorId, correlationId), ct);

    public Task<PlanYearTransitionEvent> PublishTransitionAsync(
        BenefitPlan plan, DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId, CancellationToken ct = default)
        => AppendAsync(Build(plan, PlanYearTransitionType.Transition,
            planYearEnd, nextPlanYearStart, actorId, correlationId), ct);

    private static PlanYearTransitionEvent Build(
        BenefitPlan plan, PlanYearTransitionType type,
        DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId)
    {
        var payload = new JsonObject
        {
            ["versionId"] = plan.VersionId,
            ["versionNumber"] = plan.VersionNumber,
            ["planYearType"] = plan.PlanYearDefinition?.PlanYearType.ToString(),
            ["carryoverDays"] = plan.PlanYearDefinition?.CarryoverDays
        };
        return new PlanYearTransitionEvent
        {
            EventId = PlanYearTransitionEvent.BuildEventId(type, plan.TenantId, plan.PlanId, planYearEnd),
            TransitionType = type,
            TenantId = plan.TenantId,
            PlanId = plan.PlanId,
            FromPlanYearEnd = planYearEnd,
            ToPlanYearStart = nextPlanYearStart,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
    }

    private async Task<PlanYearTransitionEvent> AppendAsync(PlanYearTransitionEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = PlanYearTransitionEvent.BuildPartitionKey(evt.TenantId, evt.PlanId);
        evt.Id = evt.EventId;
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = await GetByEventIdAsync(evt.TenantId, evt.PlanId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "PlanYearTransitionEvent {EventId} already present for {Tenant}:{Plan} (idempotent no-op)",
                LogSanitizer.Sanitize(evt.EventId), LogSanitizer.Sanitize(evt.TenantId), LogSanitizer.Sanitize(evt.PlanId));
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
                    "PlanYearTransitionEvent version {Version} conflict for {Tenant}:{Plan}; retry {Attempt}/{Max}",
                    evt.Version, LogSanitizer.Sanitize(evt.TenantId), LogSanitizer.Sanitize(evt.PlanId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append PlanYearTransitionEvent for {evt.TenantId}:{evt.PlanId} after {MaxRetries} attempts");
    }

    private async Task<PlanYearTransitionEvent?> GetByEventIdAsync(string tenantId, string planId, string eventId, CancellationToken ct)
    {
        var b = Builders<PlanYearTransitionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.PlanId, planId), b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string planId, CancellationToken ct)
    {
        var b = Builders<PlanYearTransitionEvent>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.PlanId, planId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }
}
