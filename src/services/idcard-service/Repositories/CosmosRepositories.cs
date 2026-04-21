using IdCardService.Models;
using Microsoft.Azure.Cosmos;

namespace IdCardService.Repositories;

public class CosmosIdCardOrderRepository : IIdCardOrderRepository
{
    private readonly Container _container;

    public CosmosIdCardOrderRepository(CosmosClient client, IConfiguration configuration)
    {
        var db = configuration["CosmosDb:DatabaseName"] ?? "IdCardDB";
        var container = configuration["CosmosDb:OrdersContainer"] ?? "Orders";
        _container = client.GetContainer(db, container);
    }

    public async Task UpsertAsync(IdCardOrder order, CancellationToken ct = default)
    {
        await _container.UpsertItemAsync(order, new PartitionKey(order.TenantId), cancellationToken: ct);
    }

    public async Task<IdCardOrder?> GetAsync(string tenantId, string orderId, CancellationToken ct = default)
    {
        try
        {
            var r = await _container.ReadItemAsync<IdCardOrder>(orderId, new PartitionKey(tenantId), cancellationToken: ct);
            return r.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

public class CosmosIdCardRecordRepository : IIdCardRecordRepository
{
    private readonly Container _container;

    public CosmosIdCardRecordRepository(CosmosClient client, IConfiguration configuration)
    {
        var db = configuration["CosmosDb:DatabaseName"] ?? "IdCardDB";
        var container = configuration["CosmosDb:RecordsContainer"] ?? "Records";
        _container = client.GetContainer(db, container);
    }

    public async Task UpsertAsync(IdCardRecord record, CancellationToken ct = default)
    {
        await _container.UpsertItemAsync(record, new PartitionKey(record.TenantId), cancellationToken: ct);
    }

    public async Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tid AND c.cardId = @cid")
            .WithParameter("@tid", tenantId).WithParameter("@cid", cardId);
        using var iter = _container.GetItemQueryIterator<IdCardRecord>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iter.HasMoreResults)
        {
            var resp = await iter.ReadNextAsync(ct);
            return resp.FirstOrDefault();
        }
        return null;
    }

    public async Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tid AND c.memberId = @mid ORDER BY c.issuedAt DESC")
            .WithParameter("@tid", tenantId).WithParameter("@mid", memberId);
        using var iter = _container.GetItemQueryIterator<IdCardRecord>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<IdCardRecord>();
        while (iter.HasMoreResults)
        {
            var resp = await iter.ReadNextAsync(ct);
            results.AddRange(resp);
        }
        return results;
    }

    public async Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.issuedAt >= @since")
            .WithParameter("@since", since);
        using var iter = _container.GetItemQueryIterator<IdCardRecord>(query);
        var results = new List<IdCardRecord>();
        while (iter.HasMoreResults)
        {
            var resp = await iter.ReadNextAsync(ct);
            results.AddRange(resp);
        }
        return results;
    }
}

public class CosmosIdCardTemplateRepository : IIdCardTemplateRepository
{
    private readonly Container _container;

    public CosmosIdCardTemplateRepository(CosmosClient client, IConfiguration configuration)
    {
        var db = configuration["CosmosDb:DatabaseName"] ?? "IdCardDB";
        var container = configuration["CosmosDb:TemplatesContainer"] ?? "Templates";
        _container = client.GetContainer(db, container);
    }

    public async Task UpsertAsync(IdCardTemplate template, CancellationToken ct = default)
    {
        await _container.UpsertItemAsync(template, new PartitionKey(template.TenantId), cancellationToken: ct);
    }

    public async Task<IdCardTemplate?> FindBySponsorAndPlanAsync(string tenantId, string sponsorId, string planId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tid AND c.sponsorId = @sid AND c.planId = @pid")
            .WithParameter("@tid", tenantId).WithParameter("@sid", sponsorId).WithParameter("@pid", planId);
        return await FirstOrDefaultAsync(query, tenantId, ct);
    }

    public async Task<IdCardTemplate?> FindSponsorDefaultAsync(string tenantId, string sponsorId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tid AND c.sponsorId = @sid AND NOT IS_DEFINED(c.planId)")
            .WithParameter("@tid", tenantId).WithParameter("@sid", sponsorId);
        return await FirstOrDefaultAsync(query, tenantId, ct);
    }

    public async Task<IdCardTemplate?> FindGlobalDefaultAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tid AND c.isGlobalDefault = true")
            .WithParameter("@tid", tenantId);
        return await FirstOrDefaultAsync(query, tenantId, ct);
    }

    private async Task<IdCardTemplate?> FirstOrDefaultAsync(QueryDefinition query, string tenantId, CancellationToken ct)
    {
        using var iter = _container.GetItemQueryIterator<IdCardTemplate>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iter.HasMoreResults)
        {
            var resp = await iter.ReadNextAsync(ct);
            return resp.FirstOrDefault();
        }
        return null;
    }
}
