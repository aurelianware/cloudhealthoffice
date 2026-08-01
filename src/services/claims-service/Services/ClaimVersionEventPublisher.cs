using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Services;

/// <summary>
/// Publishes <see cref="ClaimVersionEvent"/>s to the append-only
/// <c>ClaimVersionEvents</c> stream. Mirrors the provider/plan-version
/// pattern: client-supplied <see cref="ClaimVersionEvent.EventId"/> for
/// idempotency, monotonic <see cref="ClaimVersionEvent.Version"/> per
/// <c>(TenantId, ClaimVersionId)</c>.
///
/// Mongo is the system-of-record for the version stream regardless of
/// whether the main Claims store is Cosmos or Mongo — the event
/// stream lives in the same Mongo instance the existing
/// <c>ProviderVersionEvents</c> / <c>PlanVersionEvents</c> collections
/// use, so audit consumers see one consistent surface.
///
/// Kafka emission of <c>claims.pended.v1</c> / <c>claims.finalized.v1</c>
/// remains the responsibility of the existing
/// <c>IClaimEventPublisher</c>; that flow is intentionally untouched
/// in 5.1 to preserve the accumulator-service contract.
/// </summary>
public interface IClaimVersionEventPublisher
{
    Task<ClaimVersionEvent> PublishVersionSubmittedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ClaimVersionEvent> PublishVersionAdjudicatedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ClaimVersionEvent> PublishVersionPaidAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ClaimVersionEvent> PublishVersionDeniedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ClaimVersionEvent> PublishVersionSupersededAsync(Claim from, Claim to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);
    Task<ClaimVersionEvent> PublishVersionVoidedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default);

    /// <summary>
    /// Capability 5.12. Append a <c>ClaimVersionReversed</c> event for the
    /// <paramref name="version"/> being reversed (the predecessor in an
    /// adjustment workflow). Distinct from
    /// <see cref="PublishVersionSupersededAsync"/>: supersession marks
    /// the chain transition; reversal signals downstream consumers
    /// (audit/lineage, future FHIR _history, payment-service ReversalRun
    /// queue) that the prior accumulator state must be unwound.
    /// </summary>
    Task<ClaimVersionEvent> PublishVersionReversedAsync(
        Claim version,
        string supersessorVersionId,
        string adjustmentReason,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

/// <summary>Reads the append-only version stream for a single tenant-owned claim chain.</summary>
public interface IClaimVersionEventReader
{
    Task<IReadOnlyList<ClaimVersionEvent>> GetAsync(
        string tenantId,
        string claimVersionId,
        CancellationToken ct = default);
}

public sealed class MongoClaimVersionEventReader : IClaimVersionEventReader
{
    private readonly IMongoCollection<ClaimVersionEvent> _collection;

    public MongoClaimVersionEventReader(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:ClaimVersionEventsContainer"] ?? "ClaimVersionEvents";
        _collection = database.GetCollection<ClaimVersionEvent>(collectionName);
    }

    public async Task<IReadOnlyList<ClaimVersionEvent>> GetAsync(
        string tenantId,
        string claimVersionId,
        CancellationToken ct = default) =>
        await _collection
            .Find(evt => evt.TenantId == tenantId && evt.ClaimVersionId == claimVersionId)
            .SortBy(evt => evt.Version)
            .ThenBy(evt => evt.OccurredAt)
            .ToListAsync(ct);
}

public sealed class NoopClaimVersionEventReader : IClaimVersionEventReader
{
    public Task<IReadOnlyList<ClaimVersionEvent>> GetAsync(
        string tenantId,
        string claimVersionId,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClaimVersionEvent>>(Array.Empty<ClaimVersionEvent>());
}

public sealed class MongoClaimVersionEventPublisher : IClaimVersionEventPublisher
{
    private const int MaxRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMongoCollection<ClaimVersionEvent> _collection;
    private readonly ILogger<MongoClaimVersionEventPublisher> _logger;

    public MongoClaimVersionEventPublisher(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<MongoClaimVersionEventPublisher> logger)
    {
        var collectionName = configuration["CosmosDb:ClaimVersionEventsContainer"] ?? "ClaimVersionEvents";
        _collection = database.GetCollection<ClaimVersionEvent>(collectionName);
        _logger = logger;
    }

    public Task<ClaimVersionEvent> PublishVersionSubmittedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["claimNumber"] = version.ClaimNumber,
            ["submittedDate"] = version.SubmittedDate
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"submitted:{version.Id}",
            EventType = ClaimVersionEventType.ClaimVersionSubmitted,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionAdjudicatedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["adjudicatedDate"] = version.AdjudicatedDate,
            ["allowedAmount"] = version.AdjudicationResult?.AllowedAmount,
            ["payerPayment"] = version.AdjudicationResult?.PayerPayment,
            ["patientResponsibility"] = version.AdjudicationResult?.PatientResponsibility,
            ["notes"] = version.ClaimNotes
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"adjudicated:{version.Id}",
            EventType = ClaimVersionEventType.ClaimVersionAdjudicated,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionPaidAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["paidDate"] = version.PaidDate,
            ["payerPayment"] = version.AdjudicationResult?.PayerPayment,
            ["checkNumber"] = version.AdjudicationResult?.CheckNumber
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"paid:{version.Id}",
            EventType = ClaimVersionEventType.ClaimVersionPaid,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionDeniedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["adjudicatedDate"] = version.AdjudicatedDate,
            ["denialReasonCode"] = version.AdjudicationResult?.DenialReasonCode,
            ["reason"] = reason ?? version.AdjudicationResult?.DenialReason
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"denied:{version.Id}",
            EventType = ClaimVersionEventType.ClaimVersionDenied,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionSupersededAsync(Claim from, Claim to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["fromVersionId"] = from.Id,
            ["toVersionId"] = to.Id,
            ["reason"] = reason,
            ["supersededAt"] = from.SupersededAt
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"superseded:{from.Id}->{to.Id}",
            EventType = ClaimVersionEventType.ClaimVersionSuperseded,
            TenantId = from.TenantId,
            ClaimVersionId = from.ClaimVersionId,
            VersionId = from.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionVoidedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["reason"] = reason
        };

        var evt = new ClaimVersionEvent
        {
            EventId = $"voided:{version.Id}",
            EventType = ClaimVersionEventType.ClaimVersionVoided,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    public Task<ClaimVersionEvent> PublishVersionReversedAsync(
        Claim version,
        string supersessorVersionId,
        string adjustmentReason,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["versionId"] = version.Id,
            ["versionNumber"] = version.VersionNumber,
            ["supersessorVersionId"] = supersessorVersionId,
            ["adjustmentReason"] = adjustmentReason,
            ["originalCheckNumber"] = version.AdjudicationResult?.CheckNumber,
            ["originalPayerPayment"] = version.AdjudicationResult?.PayerPayment,
            ["originalPaidDate"] = version.PaidDate
        };

        var evt = new ClaimVersionEvent
        {
            // Pair the predecessor and supersessor in the event id so two
            // adjustments against the same predecessor (Phase 2) emit
            // distinct rows. Today the depth=1 invariant means at most one
            // such reversal per predecessor; the pairing is forward-compat.
            EventId = $"reversed:{version.Id}->{supersessorVersionId}",
            EventType = ClaimVersionEventType.ClaimVersionReversed,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = payload
        };
        return AppendAsync(evt, ct);
    }

    private async Task<ClaimVersionEvent> AppendAsync(ClaimVersionEvent evt, CancellationToken ct)
    {
        evt.PartitionKey = ClaimVersionEvent.BuildPartitionKey(evt.TenantId, evt.ClaimVersionId);
        // Mongo enforces global uniqueness on _id, so a deterministic EventId
        // alone (e.g. "submitted:{VersionId}") would collide if the same
        // VersionId ever appeared in a different tenant or chain (imports,
        // backfills, accidental id-reuse). Tenant-scoping the _id with the
        // partition key gives true cross-tenant isolation while preserving
        // the deterministic-EventId idempotency story (the unique index on
        // (TenantId, ClaimVersionId, EventId) is what callers rely on).
        evt.Id = $"{evt.PartitionKey}:{evt.EventId}";
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        // Per-hop timing for the three Mongo round-trips in this method.
        // Part 10/11 disclosed the Submit chain's five sequential I/O hops as
        // a known, unfixed bottleneck without ever measuring which one
        // dominated. It turned out to be none of them individually -- all
        // three showed the same low-median, huge-P95 shape, tracing back to
        // MongoDB's own CPU limit being undersized for the number of
        // services sharing it, not a cost inherent to any single hop.
        var profileSw = Stopwatch.StartNew();
        var existing = await GetByEventIdAsync(evt.TenantId, evt.ClaimVersionId, evt.EventId, ct);
        var idempotencyCheckMs = profileSw.Elapsed.TotalMilliseconds;
        if (existing != null)
        {
            _logger.LogDebug(
                "ClaimVersionEvent {EventId} already present for {Tenant}:{ClaimVersionId} (idempotent no-op)",
                Sanitize(evt.EventId), Sanitize(evt.TenantId), Sanitize(evt.ClaimVersionId));
            return existing;
        }

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            profileSw.Restart();
            evt.Version = await GetNextVersionAsync(evt.TenantId, evt.ClaimVersionId, ct);
            var versionQueryMs = profileSw.Elapsed.TotalMilliseconds;
            try
            {
                profileSw.Restart();
                await _collection.InsertOneAsync(evt, cancellationToken: ct);
                var insertMs = profileSw.Elapsed.TotalMilliseconds;
                _logger.LogDebug(
                    "SubmitProfile.VersionEvent idempotencyCheckMs={IdempotencyCheckMs} versionQueryMs={VersionQueryMs} insertMs={InsertMs}",
                    idempotencyCheckMs, versionQueryMs, insertMs);
                return evt;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var refetch = await GetByEventIdAsync(evt.TenantId, evt.ClaimVersionId, evt.EventId, ct);
                if (refetch != null) return refetch;

                _logger.LogWarning(
                    "ClaimVersionEvent version {Version} conflict for {Tenant}:{ClaimVersionId}; retry {Attempt}/{Max}",
                    evt.Version, Sanitize(evt.TenantId), Sanitize(evt.ClaimVersionId), attempt + 1, MaxRetries);

                if (attempt + 1 < MaxRetries)
                    await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new InvalidOperationException(
            $"Failed to append ClaimVersionEvent for {evt.TenantId}:{evt.ClaimVersionId} after {MaxRetries} attempts");
    }

    private async Task<ClaimVersionEvent?> GetByEventIdAsync(string tenantId, string claimVersionId, string eventId, CancellationToken ct)
    {
        var b = Builders<ClaimVersionEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ClaimVersionId, claimVersionId),
            b.Eq(x => x.EventId, eventId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    private async Task<int> GetNextVersionAsync(string tenantId, string claimVersionId, CancellationToken ct)
    {
        var b = Builders<ClaimVersionEvent>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ClaimVersionId, claimVersionId));
        var latest = await _collection.Find(filter).SortByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        return (latest?.Version ?? 0) + 1;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// No-op fallback used when no Mongo instance is available (e.g. Cosmos-only
/// dev environments without the events stream provisioned). Logs a warning so
/// ops can spot the missing wiring; never blocks the claim write path.
/// Mirrors <c>NoopProviderVersionEventPublisher</c>.
/// </summary>
public sealed class NoopClaimVersionEventPublisher : IClaimVersionEventPublisher
{
    private readonly ILogger<NoopClaimVersionEventPublisher> _logger;

    public NoopClaimVersionEventPublisher(ILogger<NoopClaimVersionEventPublisher> logger) => _logger = logger;

    public Task<ClaimVersionEvent> PublishVersionSubmittedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"submitted:{version.Id}", ClaimVersionEventType.ClaimVersionSubmitted, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionAdjudicatedAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"adjudicated:{version.Id}", ClaimVersionEventType.ClaimVersionAdjudicated, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionPaidAsync(Claim version, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"paid:{version.Id}", ClaimVersionEventType.ClaimVersionPaid, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionDeniedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"denied:{version.Id}", ClaimVersionEventType.ClaimVersionDenied, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionSupersededAsync(Claim from, Claim to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(from, $"superseded:{from.Id}->{to.Id}", ClaimVersionEventType.ClaimVersionSuperseded, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionVoidedAsync(Claim version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => DropAndReturn(version, $"voided:{version.Id}", ClaimVersionEventType.ClaimVersionVoided, actorId, correlationId);

    public Task<ClaimVersionEvent> PublishVersionReversedAsync(
        Claim version,
        string supersessorVersionId,
        string adjustmentReason,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
        => DropAndReturn(version, $"reversed:{version.Id}->{supersessorVersionId}", ClaimVersionEventType.ClaimVersionReversed, actorId, correlationId);

    private Task<ClaimVersionEvent> DropAndReturn(Claim version, string eventId, ClaimVersionEventType type, string? actorId, string? correlationId)
    {
        _logger.LogWarning(
            "ClaimVersionEventPublisher is not configured; dropping {EventType} for claim {ClaimVersionId} version {VersionId}",
            type, version.ClaimVersionId, version.Id);
        return Task.FromResult(new ClaimVersionEvent
        {
            EventId = eventId,
            EventType = type,
            TenantId = version.TenantId,
            ClaimVersionId = version.ClaimVersionId,
            VersionId = version.Id,
            ActorId = actorId,
            CorrelationId = correlationId
        });
    }
}
