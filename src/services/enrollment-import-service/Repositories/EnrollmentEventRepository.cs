using System.Net;
using EnrollmentImportService.Models;
using Microsoft.Azure.Cosmos;

namespace EnrollmentImportService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="EnrollmentEvent"/>.
///
/// Container provisioning:
///   - Partition key path: <c>/partitionKey</c> (format <c>{tenantId}:{memberId}</c>).
///   - Unique-key policy: <c>/version</c> — concurrent writers collide at the index
///     instead of silently overlapping.
/// </summary>
public class EnrollmentEventRepository : IEnrollmentEventRepository
{
    /// <summary>Cosmos sub-status for a unique-key constraint violation.</summary>
    public const int UniqueKeyViolationSubStatus = 1009;

    private readonly CosmosClient _cosmosClient;
    private readonly IConfiguration _config;
    private readonly ILogger<EnrollmentEventRepository>? _logger;

    public EnrollmentEventRepository(
        CosmosClient cosmosClient,
        IConfiguration config,
        ILogger<EnrollmentEventRepository>? logger = null)
    {
        _cosmosClient = cosmosClient;
        _config = config;
        _logger = logger;
    }

    private Container Container => _cosmosClient.GetContainer(
        _config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        _config["CosmosDb:EnrollmentEventsContainerName"] ?? "enrollment-events");

    public async Task<EnrollmentEventAppendResult> AppendAsync(EnrollmentEvent evt, CancellationToken ct = default)
    {
        Normalize(evt);

        try
        {
            var response = await Container.CreateItemAsync(
                evt,
                new PartitionKey(evt.PartitionKey),
                cancellationToken: ct);
            return new EnrollmentEventAppendResult(response.Resource, Appended: true);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            if (ex.SubStatusCode == UniqueKeyViolationSubStatus)
            {
                return new EnrollmentEventAppendResult(evt, Appended: false);
            }

            if (ex.SubStatusCode != 0)
            {
                _logger?.LogWarning(ex,
                    "Unexpected Cosmos 409 SubStatus {SubStatus} on enrollment-events append for {PartitionKey} v{Version}. Treating as idempotent no-op.",
                    ex.SubStatusCode, SanitizeForLog(evt.PartitionKey), evt.Version);
            }

            var existing = await GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            return new EnrollmentEventAppendResult(existing ?? evt, Appended: false);
        }
    }

    public async Task<EnrollmentEventPage> ListByMemberAsync(
        string tenantId, string memberId, EnrollmentEventQuery query, CancellationToken ct = default)
    {
        var partitionKey = EnrollmentEvent.BuildPartitionKey(tenantId, memberId);

        var sql = "SELECT * FROM c WHERE c.partitionKey = @pk";
        if (query.EventType.HasValue) sql += " AND c.eventType = @type";
        if (query.FromUtc.HasValue) sql += " AND c.occurredAt >= @from";
        if (query.ToUtc.HasValue) sql += " AND c.occurredAt <= @to";
        sql += " ORDER BY c.version DESC";

        var def = new QueryDefinition(sql).WithParameter("@pk", partitionKey);
        if (query.EventType.HasValue) def = def.WithParameter("@type", (int)query.EventType.Value);
        if (query.FromUtc.HasValue) def = def.WithParameter("@from", query.FromUtc.Value);
        if (query.ToUtc.HasValue) def = def.WithParameter("@to", query.ToUtc.Value);

        var iterator = Container.GetItemQueryIterator<EnrollmentEvent>(
            def,
            continuationToken: query.ContinuationToken,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey),
                MaxItemCount = Math.Clamp(query.Limit, 1, 500)
            });

        if (!iterator.HasMoreResults)
            return new EnrollmentEventPage(Array.Empty<EnrollmentEvent>(), null);

        var page = await iterator.ReadNextAsync(ct);
        return new EnrollmentEventPage(page.ToList(), page.ContinuationToken);
    }

    public async Task<EnrollmentEvent?> GetByIdAsync(
        string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        try
        {
            var response = await Container.ReadItemAsync<EnrollmentEvent>(
                eventId,
                new PartitionKey(EnrollmentEvent.BuildPartitionKey(tenantId, memberId)),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var partitionKey = EnrollmentEvent.BuildPartitionKey(tenantId, memberId);
        var query = new QueryDefinition(
            "SELECT VALUE MAX(c.version) FROM c WHERE c.partitionKey = @pk")
            .WithParameter("@pk", partitionKey);

        var iterator = Container.GetItemQueryIterator<int?>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            var max = page.FirstOrDefault();
            return (max ?? 0) + 1;
        }
        return 1;
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
