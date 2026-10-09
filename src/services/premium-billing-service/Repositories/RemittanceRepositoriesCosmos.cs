using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

// Cosmos DB implementations. Containers are partitioned by /tenantId like
// PremiumInvoices; the service serializer writes camelCase names and camelCase
// enum strings, so enum filters bind the camelCase spelling.

public sealed class RemittanceBatchRepositoryCosmos : IRemittanceBatchRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public RemittanceBatchRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", "RemittanceBatches");
        _http = http;
    }

    public async Task<RemittanceBatch?> GetByIdAsync(string id)
    {
        try
        {
            return (await _container.ReadItemAsync<RemittanceBatch>(id, new PartitionKey(RepositoryTenant.From(_http)))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> TryCreateAsync(RemittanceBatch batch)
    {
        batch.TenantId = RepositoryTenant.From(_http);
        try
        {
            await _container.CreateItemAsync(batch, new PartitionKey(batch.TenantId));
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<RemittanceBatch> UpdateAsync(RemittanceBatch batch) =>
        (await _container.ReplaceItemAsync(batch, batch.Id, new PartitionKey(batch.TenantId))).Resource;

    public async Task<IEnumerable<RemittanceBatch>> SearchAsync(DateTime? receivedFrom = null, DateTime? receivedTo = null, int page = 1, int pageSize = 50)
    {
        var text = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        if (receivedFrom.HasValue) text += " AND c.receivedAt >= @from";
        if (receivedTo.HasValue) text += " AND c.receivedAt <= @to";
        text += $" ORDER BY c.receivedAt DESC OFFSET {(Math.Max(page, 1) - 1) * pageSize} LIMIT {pageSize}";
        var query = new QueryDefinition(text).WithParameter("@tenantId", RepositoryTenant.From(_http));
        if (receivedFrom.HasValue) query = query.WithParameter("@from", receivedFrom.Value);
        if (receivedTo.HasValue) query = query.WithParameter("@to", receivedTo.Value);
        return await CosmosQuery.ToListAsync<RemittanceBatch>(_container, query);
    }
}

public sealed class RemittanceExceptionRepositoryCosmos : IRemittanceExceptionRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public RemittanceExceptionRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", "RemittanceExceptions");
        _http = http;
    }

    public async Task<RemittanceException?> GetByIdAsync(string id)
    {
        try
        {
            return (await _container.ReadItemAsync<RemittanceException>(id, new PartitionKey(RepositoryTenant.From(_http)))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<RemittanceException>> ListAsync(RemittanceExceptionStatus? status = null, int page = 1, int pageSize = 50)
    {
        var text = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        if (status.HasValue) text += " AND c.status = @status";
        text += $" ORDER BY c.createdAt OFFSET {(Math.Max(page, 1) - 1) * pageSize} LIMIT {pageSize}";
        var query = new QueryDefinition(text).WithParameter("@tenantId", RepositoryTenant.From(_http));
        if (status.HasValue)
            query = query.WithParameter("@status", JsonNamingPolicy.CamelCase.ConvertName(status.Value.ToString()));
        return await CosmosQuery.ToListAsync<RemittanceException>(_container, query);
    }

    public async Task<RemittanceException> CreateAsync(RemittanceException exception)
    {
        exception.TenantId = RepositoryTenant.From(_http);
        return (await _container.CreateItemAsync(exception, new PartitionKey(exception.TenantId))).Resource;
    }

    public async Task<RemittanceException> UpdateAsync(RemittanceException exception) =>
        (await _container.ReplaceItemAsync(exception, exception.Id, new PartitionKey(exception.TenantId))).Resource;
}

public sealed class SponsorAccountRepositoryCosmos : ISponsorAccountRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public SponsorAccountRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", "SponsorAccounts");
        _http = http;
    }

    public async Task<SponsorAccount?> GetAsync(string groupNumber)
    {
        var tenant = RepositoryTenant.From(_http);
        try
        {
            var response = await _container.ReadItemAsync<SponsorAccount>(SponsorAccount.IdFor(tenant, groupNumber), new PartitionKey(tenant));
            response.Resource.ETag = response.ETag;
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<SponsorAccount> UpdateAsync(string groupNumber, Action<SponsorAccount> change)
    {
        var tenant = RepositoryTenant.From(_http);
        for (var attempt = 0; attempt < RepositoryTenant.MaxConcurrencyAttempts; attempt++)
        {
            var account = await GetAsync(groupNumber);
            try
            {
                if (account == null)
                {
                    account = new SponsorAccount { Id = SponsorAccount.IdFor(tenant, groupNumber), TenantId = tenant, GroupNumber = groupNumber };
                    change(account);
                    account.Version = 1;
                    account.LastUpdatedAt = DateTime.UtcNow;
                    var created = await _container.CreateItemAsync(account, new PartitionKey(tenant));
                    created.Resource.ETag = created.ETag;
                    return created.Resource;
                }

                var etag = account.ETag;
                change(account);
                account.Version++;
                account.LastUpdatedAt = DateTime.UtcNow;
                var replaced = await _container.ReplaceItemAsync(account, account.Id, new PartitionKey(tenant),
                    new ItemRequestOptions { IfMatchEtag = etag });
                replaced.Resource.ETag = replaced.ETag;
                return replaced.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                // Someone else wrote first: re-read and apply the change on top.
            }
        }
        throw new ConcurrencyConflictException($"Sponsor account {groupNumber} kept changing; the update was not saved");
    }
}

internal static class CosmosQuery
{
    public static async Task<List<T>> ToListAsync<T>(Container container, QueryDefinition query)
    {
        var iterator = container.GetItemQueryIterator<T>(query);
        var results = new List<T>();
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }
}
