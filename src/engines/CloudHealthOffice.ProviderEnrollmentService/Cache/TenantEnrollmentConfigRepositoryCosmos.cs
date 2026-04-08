using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Cosmos DB implementation of ITenantEnrollmentConfigRepository.
///
/// Container: enrollment-tenant-config
/// Partition key: /tenantId
/// Document ID:   tenantId  — one document per tenant, O(1) point reads
///
/// No TTL — config documents are permanent until explicitly deleted or replaced.
/// Container should be provisioned with autoscale (low RU ceiling — reads are
/// rare after Redis caching is in place; writes are infrequent admin operations).
/// </summary>
public sealed class TenantEnrollmentConfigRepositoryCosmos : ITenantEnrollmentConfigRepository
{
    private readonly Container _container;
    private readonly ILogger<TenantEnrollmentConfigRepositoryCosmos> _logger;

    public TenantEnrollmentConfigRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<TenantEnrollmentConfigRepositoryCosmos> logger)
    {
        var databaseName  = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["ProviderEnrollmentService:TenantConfigContainer"]
                            ?? "enrollment-tenant-config";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger    = logger;
    }

    public async Task<TenantEnrollmentConfig?> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<TenantEnrollmentConfigDocument>(
                tenantId,
                new PartitionKey(tenantId),
                cancellationToken: ct);

            return response.Resource.ToModel();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertAsync(
        TenantEnrollmentConfig config, CancellationToken ct = default)
    {
        var doc = TenantEnrollmentConfigDocument.FromModel(config);

        await _container.UpsertItemAsync(
            doc,
            new PartitionKey(config.TenantId),
            cancellationToken: ct);

        _logger.LogInformation(
            "TenantEnrollmentConfig upserted for tenant {TenantId} " +
            "with {LobOverrideCount} LOB overrides",
            config.TenantId, config.LobOverrides.Count);
    }

    public async Task DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        try
        {
            await _container.DeleteItemAsync<TenantEnrollmentConfigDocument>(
                tenantId,
                new PartitionKey(tenantId),
                cancellationToken: ct);

            _logger.LogInformation(
                "TenantEnrollmentConfig deleted for tenant {TenantId}", tenantId);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent — already gone
        }
    }

    public async Task<IReadOnlyList<TenantEnrollmentConfig>> ListAsync(
        CancellationToken ct = default)
    {
        // Cross-partition query — acceptable for the admin portal grid
        // (low frequency, not on any hot path)
        var query    = new QueryDefinition("SELECT * FROM c ORDER BY c.tenantId");
        var iterator = _container.GetItemQueryIterator<TenantEnrollmentConfigDocument>(query);
        var results  = new List<TenantEnrollmentConfig>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(d => d.ToModel()));
        }

        return results;
    }
}
