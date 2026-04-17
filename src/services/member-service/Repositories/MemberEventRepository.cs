using System.Net;
using MemberService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="MemberEvent"/>.
/// Container is provisioned with PK path <c>/partitionKey</c> so per-member streams
/// remain co-located for Change Feed consumers.
/// </summary>
public class MemberEventRepository : IMemberEventRepository
{
    private readonly Container _container;

    public MemberEventRepository(CosmosClient cosmosClient, string databaseName, string containerName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(containerName);
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
}
