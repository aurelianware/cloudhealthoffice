using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Repositories;

/// <summary>
/// MongoDB repository for <see cref="EnrollmentEvent"/>. Idempotency and
/// ordering are enforced by unique compound indexes created at startup by
/// <c>EnrollmentIndexInitializer</c> (not here — keeping construction
/// side-effect free so the repository can be registered as a singleton),
/// same approach as member-service's MemberEventRepositoryMongo:
///   - unique (tenantId, memberId, eventId) — EventId collisions no-op.
///   - unique (tenantId, memberId, version) — concurrent writers collide on
///     the version slot instead of silently overlapping.
///
/// A single duplicate-key catch handles both: on any collision, looking up
/// by (tenantId, memberId, eventId) returns the winning document for a real
/// EventId collision, or null for a version collision (no document exists
/// under *this* eventId), in which case the caller gets back its own
/// in-memory envelope — matching the interface's documented contract
/// without needing to inspect which index actually fired.
/// </summary>
public class EnrollmentEventRepository : IEnrollmentEventRepository
{
    private readonly IMongoCollection<EnrollmentEvent> _collection;
    private readonly ILogger<EnrollmentEventRepository>? _logger;

    public EnrollmentEventRepository(
        IMongoDatabase database,
        ILogger<EnrollmentEventRepository>? logger = null)
    {
        _collection = database.GetCollection<EnrollmentEvent>("enrollment-events");
        _logger = logger;
    }

    public async Task<EnrollmentEventAppendResult> AppendAsync(EnrollmentEvent evt, CancellationToken ct = default)
    {
        Normalize(evt);

        try
        {
            await _collection.InsertOneAsync(evt, cancellationToken: ct);
            return new EnrollmentEventAppendResult(evt, Appended: true);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            if (existing is null)
            {
                _logger?.LogWarning(
                    "Version collision on enrollment-events append for {PartitionKey} v{Version}; caller should retry with a fresh version.",
                    SanitizeForLog(evt.PartitionKey), evt.Version);
            }
            return new EnrollmentEventAppendResult(existing ?? evt, Appended: false);
        }
    }

    public async Task<EnrollmentEventPage> ListByMemberAsync(
        string tenantId, string memberId, EnrollmentEventQuery query, CancellationToken ct = default)
    {
        var builder = Builders<EnrollmentEvent>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId) & builder.Eq(x => x.MemberId, memberId);

        if (query.EventType.HasValue)
            filter &= builder.Eq(x => x.EventType, query.EventType.Value);
        if (query.FromUtc.HasValue)
            filter &= builder.Gte(x => x.OccurredAt, query.FromUtc.Value);
        if (query.ToUtc.HasValue)
            filter &= builder.Lte(x => x.OccurredAt, query.ToUtc.Value);

        var limit = Math.Clamp(query.Limit, 1, 500);
        var skip = 0;
        if (!string.IsNullOrEmpty(query.ContinuationToken) && int.TryParse(query.ContinuationToken, out var parsedSkip))
        {
            skip = parsedSkip;
        }

        var items = await _collection.Find(filter)
            .SortByDescending(x => x.Version)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync(ct);

        var nextToken = items.Count == limit ? (skip + limit).ToString() : null;
        return new EnrollmentEventPage(items, nextToken);
    }

    public async Task<EnrollmentEvent?> GetByIdAsync(
        string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        var builder = Builders<EnrollmentEvent>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId) &
                     builder.Eq(x => x.MemberId, memberId) &
                     builder.Eq(x => x.EventId, eventId);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var builder = Builders<EnrollmentEvent>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId) & builder.Eq(x => x.MemberId, memberId);

        var last = await _collection.Find(filter)
            .SortByDescending(x => x.Version)
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        return (last?.Version ?? 0) + 1;
    }

    private static void Normalize(EnrollmentEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.MemberId))
            throw new ArgumentException("TenantId and MemberId are required");

        if (string.IsNullOrEmpty(evt.Id)) evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = EnrollmentEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
