using System.Net;
using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>
/// Stored rate table versions of the request's tenant. Versions are only ever
/// inserted, never replaced or deleted: an invoice line's
/// <c>(RateTableId, RateTableVersion)</c> must keep resolving to the rates it
/// was billed with.
/// </summary>
public interface IRateTableRepository
{
    /// <summary>Every version of every rate table of the tenant.</summary>
    Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync();

    /// <summary>Every version of one rate table, oldest first.</summary>
    Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId);

    Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version);

    /// <summary>
    /// Inserts a version. Throws <see cref="RateTableVersionConflictException"/>
    /// when that version of the table already exists.
    /// </summary>
    Task<RateTableRecord> CreateVersionAsync(RateTableRecord record);
}

public sealed class RateTableVersionConflictException : Exception
{
    public RateTableVersionConflictException(string rateTableId, int version)
        : base($"Version {version} of rate table {rateTableId} already exists; re-read the table and retry")
    {
    }
}

/// <summary>Cosmos DB: container <c>RateTables</c>, partition key <c>/tenantId</c>.</summary>
public sealed class RateTableRepositoryCosmos : IRateTableRepository
{
    public const string ContainerName = "RateTables";

    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public RateTableRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", ContainerName);
        _http = http;
    }

    public async Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync()
    {
        var tenantId = RepositoryTenant.From(_http);
        return await QueryAsync(new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId")
            .WithParameter("@tenantId", tenantId), tenantId);
    }

    public async Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId)
    {
        var tenantId = RepositoryTenant.From(_http);
        var list = await QueryAsync(new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.rateTableId = @rateTableId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@rateTableId", rateTableId), tenantId);
        return list.OrderBy(r => r.Version).ToList();
    }

    public async Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version)
    {
        var tenantId = RepositoryTenant.From(_http);
        try
        {
            var response = await _container.ReadItemAsync<RateTableRecord>(
                RateTableRecord.DocumentId(tenantId, rateTableId, version), new PartitionKey(tenantId));
            // The id is a hash: make sure it is this table (and tenant).
            var record = response.Resource;
            return record.TenantId == tenantId && record.RateTableId == rateTableId && record.Version == version ? record : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<RateTableRecord> CreateVersionAsync(RateTableRecord record)
    {
        record.TenantId = RepositoryTenant.From(_http);
        record.Id = RateTableRecord.DocumentId(record.TenantId, record.RateTableId, record.Version);
        try
        {
            var response = await _container.CreateItemAsync(record, new PartitionKey(record.TenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new RateTableVersionConflictException(record.RateTableId, record.Version);
        }
    }

    private async Task<List<RateTableRecord>> QueryAsync(QueryDefinition query, string tenantId)
    {
        var iterator = _container.GetItemQueryIterator<RateTableRecord>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<RateTableRecord>();
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }
}
