using System.Net;
using AppealsService.Models;
using Microsoft.Azure.Cosmos;

namespace AppealsService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IAppealEventRepository"/> and
/// <see cref="IAppealEventSink"/>. Partition key: <c>/partitionKey</c>
/// (<c>{tenantId}:{appealId}</c>) so the full audit trail for an appeal
/// lives in a single partition and scans cheaply.
///
/// Writes are idempotent: the Cosmos document id is
/// <see cref="AppealEvent.EventId"/>, so a duplicate append with the same
/// EventId returns a 409 that we treat as a no-op — safe under retry from
/// Kafka / controller layers.
/// </summary>
public sealed class AppealEventRepository : IAppealEventRepository, IAppealEventSink
{
    public const string AppealEventsContainerName = "AppealEvents";

    private readonly Container _container;

    public AppealEventRepository(CosmosClient cosmosClient, string databaseName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(AppealEventsContainerName);
    }

    public async Task AppendAsync(AppealEvent evt, CancellationToken ct = default)
    {
        NormalizeEnvelope(evt);
        try
        {
            await _container.CreateItemAsync(evt, new PartitionKey(evt.PartitionKey), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Idempotent append: duplicate EventId = same append, ignore.
        }
    }

    public async Task<IReadOnlyList<AppealEvent>> ListByAppealAsync(
        string tenantId, string appealId, CancellationToken ct = default)
    {
        var partitionKey = AppealEvent.BuildPartitionKey(tenantId, appealId);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.partitionKey = @pk ORDER BY c.occurredAt ASC")
            .WithParameter("@pk", partitionKey);

        var iterator = _container.GetItemQueryIterator<AppealEvent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        var results = new List<AppealEvent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    internal static void NormalizeEnvelope(AppealEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.AppealId))
            throw new ArgumentException("TenantId and AppealId are required");

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = AppealEvent.BuildPartitionKey(evt.TenantId, evt.AppealId);
    }
}
