using System.Net;
using Microsoft.Azure.Cosmos;
using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>Cosmos DB workspace store; containers partitioned on <c>/tenantId</c>.</summary>
public sealed class CosmosWorkspaceStore : IWorkspaceStore
{
    private readonly Container _notices;
    private readonly Container _reads;
    private readonly Container _pins;

    public CosmosWorkspaceStore(CosmosClient client, CosmosWorkStoreOptions options)
    {
        _notices = client.GetContainer(options.DatabaseName, options.NoticesContainer);
        _reads = client.GetContainer(options.DatabaseName, options.NoticeReadsContainer);
        _pins = client.GetContainer(options.DatabaseName, options.PinsContainer);
    }

    private static async Task<List<T>> QueryAsync<T>(Container container, QueryDefinition query, string tenantId, CancellationToken ct)
    {
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync(ct));
        return results;
    }

    private static QueryDefinition ByUser(string tenantId, string userId) =>
        new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t AND c.userId = @u")
            .WithParameter("@t", tenantId).WithParameter("@u", userId);

    public async Task<IReadOnlyList<PlanNotice>> ListNoticesAsync(string tenantId, CancellationToken ct = default) =>
        await QueryAsync<PlanNotice>(_notices,
            new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t").WithParameter("@t", tenantId), tenantId, ct);

    public async Task<PlanNotice?> GetNoticeAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            return (await _notices.ReadItemAsync<PlanNotice>(id, new PartitionKey(tenantId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task UpsertNoticeAsync(PlanNotice notice, CancellationToken ct = default) =>
        _notices.UpsertItemAsync(notice, new PartitionKey(notice.TenantId), cancellationToken: ct);

    public async Task<IReadOnlySet<string>> ReadNoticeIdsAsync(string tenantId, string userId, CancellationToken ct = default) =>
        (await QueryAsync<NoticeRead>(_reads, ByUser(tenantId, userId), tenantId, ct)).Select(r => r.NoticeId).ToHashSet();

    public Task MarkReadAsync(NoticeRead read, CancellationToken ct = default) =>
        _reads.UpsertItemAsync(read, new PartitionKey(read.TenantId), cancellationToken: ct);

    public async Task<IReadOnlyList<PinnedRecord>> ListPinsAsync(string tenantId, string userId, CancellationToken ct = default) =>
        await QueryAsync<PinnedRecord>(_pins, ByUser(tenantId, userId), tenantId, ct);

    public Task UpsertPinAsync(PinnedRecord pin, CancellationToken ct = default) =>
        _pins.UpsertItemAsync(pin, new PartitionKey(pin.TenantId), cancellationToken: ct);

    public async Task<bool> DeletePinAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            await _pins.DeleteItemAsync<PinnedRecord>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
