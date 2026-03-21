using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Persistence;

/// <summary>
/// Cosmos DB-backed accumulator repository.
///
/// Optimistic concurrency: Cosmos provides ETag-based concurrency out of
/// the box. We store the ETag from each read/write in document.CosmosETag
/// (JsonIgnore — not persisted in the document body) and pass it as
/// IfMatchEtag on the next replace. PreconditionFailed (412) or Conflict
/// (409) means another claim won the race; we throw OptimisticConcurrencyException.
///
/// Partition key: tenantId — keeps all accumulators for a tenant co-located,
/// which is efficient for the plan-year reset query.
///
/// Container settings recommendation:
///   Partition key: /tenantId
///   Indexing: include /benefitPlanId and /planYear for the reset query.
///   TTL: not recommended (accumulator history should not auto-expire).
/// </summary>
public class AccumulatorRepositoryCosmos : IAccumulatorRepository
{
    private readonly Container _container;
    private readonly ILogger<AccumulatorRepositoryCosmos> _logger;

    public AccumulatorRepositoryCosmos(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<AccumulatorRepositoryCosmos> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["BenefitEngine:AccumulatorContainer"] ?? "Accumulators";
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public async Task<AccumulatorDocument?> GetAsync(
        string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        var id = AccumulatorDocument.MakeId(tenantId, scope.ToString(), ownerId, benefitPlanId, planYear);

        try
        {
            var response = await _container.ReadItemAsync<AccumulatorDocument>(
                id, new PartitionKey(tenantId), cancellationToken: ct);

            var doc = response.Resource;
            doc.CosmosETag = response.ETag;
            return doc;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AccumulatorDocument> UpsertAsync(
        AccumulatorDocument document,
        CancellationToken ct = default)
    {
        document.LastUpdated = DateTime.UtcNow;

        try
        {
            ItemResponse<AccumulatorDocument> response;

            if (document.Version == 0)
            {
                // New document — create. Conflict (409) = concurrent insert won the race.
                document.Version = 1;
                response = await _container.CreateItemAsync(
                    document,
                    new PartitionKey(document.TenantId),
                    cancellationToken: ct);

                _logger.LogDebug("Created new Cosmos accumulator document {DocId}", SanitizeForLog(document.Id));
            }
            else
            {
                // Existing document — replace with ETag guard.
                document.Version++;
                response = await _container.ReplaceItemAsync(
                    document,
                    document.Id,
                    new PartitionKey(document.TenantId),
                    new ItemRequestOptions { IfMatchEtag = document.CosmosETag },
                    ct);
            }

            var saved = response.Resource;
            saved.CosmosETag = response.ETag;
            return saved;
        }
        catch (CosmosException ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed ||
            ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogDebug(
                "Cosmos concurrency conflict on {DocId}: {Status}",
                SanitizeForLog(document.Id), ex.StatusCode);
            throw new OptimisticConcurrencyException(document.Id);
        }
    }

    public async Task DeleteByPlanYearAsync(
        string tenantId, Guid benefitPlanId, string planYear,
        CancellationToken ct = default)
    {
        // Query for IDs within this partition, then delete each one.
        // We use a projection to avoid fetching the full document body.
        var query = new QueryDefinition(
            "SELECT c.id FROM c " +
            "WHERE c.tenantId = @tenantId " +
            "AND c.benefitPlanId = @planId " +
            "AND c.planYear = @planYear")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", benefitPlanId.ToString())
            .WithParameter("@planYear", planYear);

        var iterator = _container.GetItemQueryIterator<dynamic>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        int deleted = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var item in page)
            {
                string id = item.id.ToString();
                await _container.DeleteItemAsync<AccumulatorDocument>(
                    id, new PartitionKey(tenantId), cancellationToken: ct);
                deleted++;
            }
        }

        _logger.LogInformation(
            "Deleted {Count} Cosmos accumulator documents for plan {PlanId} / year {PlanYear}",
            deleted, benefitPlanId, planYear);
    }
}
