using System.Net;
using MemberService.Models;
using MemberService.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="MemberEvent"/>.
///
/// The container MUST be provisioned with:
///   - Partition key path: <c>/partitionKey</c> (format <c>{tenantId}:{memberId}</c>).
///   - Unique key policy with path <c>/version</c>. This enforces
///     uniqueness of <c>(partitionKey, version)</c> so concurrent writers
///     conflict at the index level, not silently overlap.
///
/// See <c>scripts/cosmos/provision-member-events.sh</c>.
/// </summary>
public class MemberEventRepository : IMemberEventRepository
{
    /// <summary>
    /// Cosmos sub-status for a unique-key constraint violation on a secondary
    /// unique-key policy path. An HTTP 409 with this sub-status means a
    /// concurrent writer already claimed this version slot; the publisher
    /// should refetch the next version and retry.
    /// </summary>
    public const int UniqueKeyViolationSubStatus = 1009;

    private readonly Container _container;
    private readonly ILogger<MemberEventRepository>? _logger;

    public MemberEventRepository(
        CosmosClient cosmosClient,
        string databaseName,
        string containerName,
        ILogger<MemberEventRepository>? logger = null)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(containerName);
        _logger = logger;
    }

    public async Task<AppendResult> AppendAsync(MemberEvent evt, CancellationToken ct = default)
    {
        NormalizeEnvelope(evt);

        try
        {
            var response = await _container.CreateItemAsync(
                evt,
                new PartitionKey(evt.PartitionKey),
                cancellationToken: ct);
            return new AppendResult(response.Resource, Appended: true);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Two distinct causes of 409 on this container:
            //   - SubStatus 0: document id collision → client retried with same EventId
            //     (the id is EventId). Idempotent no-op; return the existing event.
            //   - SubStatus 1009: unique-key-policy violation on /version → a concurrent
            //     writer took our version slot. Surface as Appended=false with Event=null
            //     so the publisher recomputes Version and retries.
            //   - Any other non-zero sub-status: log and treat as id-collision (idempotent)
            //     to stay safe, but the log entry lets us notice if Cosmos ever introduces
            //     a new sub-status we haven't considered.
            if (ex.SubStatusCode == UniqueKeyViolationSubStatus)
            {
                return new AppendResult(evt, Appended: false);
            }

            if (ex.SubStatusCode != 0)
            {
                _logger?.LogWarning(ex,
                    "Unexpected Cosmos 409 SubStatus {SubStatus} on member-events append for {PartitionKey} v{Version}. Treating as idempotent no-op.",
                    ex.SubStatusCode, SanitizeForLog(evt.PartitionKey), evt.Version);
            }

            var existing = await GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            return new AppendResult(existing ?? evt, Appended: false);
        }
    }

    public async Task<IReadOnlyList<MemberEvent>> ListByMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var partitionKey = MemberEvent.BuildPartitionKey(tenantId, memberId);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.partitionKey = @pk ORDER BY c.version ASC")
            .WithParameter("@pk", partitionKey);

        var iterator = _container.GetItemQueryIterator<MemberEvent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        var results = new List<MemberEvent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<MemberEvent?> GetByIdAsync(
        string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<MemberEvent>(
                eventId,
                new PartitionKey(MemberEvent.BuildPartitionKey(tenantId, memberId)),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<int> GetNextVersionAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var partitionKey = MemberEvent.BuildPartitionKey(tenantId, memberId);
        var query = new QueryDefinition(
            "SELECT VALUE MAX(c.version) FROM c WHERE c.partitionKey = @pk")
            .WithParameter("@pk", partitionKey);

        var iterator = _container.GetItemQueryIterator<int?>(
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

    private static void NormalizeEnvelope(MemberEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.MemberId))
            throw new ArgumentException("TenantId and MemberId are required");

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = MemberEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
