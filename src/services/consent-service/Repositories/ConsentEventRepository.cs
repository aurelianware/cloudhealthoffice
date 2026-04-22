using System.Net;
using ConsentService.Models;
using Microsoft.Azure.Cosmos;

namespace ConsentService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IConsentEventRepository"/> and
/// <see cref="IConsentEventSink"/>. Partition key: <c>/partitionKey</c>
/// (<c>{tenantId}:{consentId}</c>) so the full audit trail for a consent
/// lives in a single partition and scans cheaply.
///
/// Writes are idempotent: the Cosmos document id is <see cref="ConsentEvent.EventId"/>,
/// so a duplicate append with the same EventId returns a 409 that we treat
/// as a no-op — safe under retry from Kafka / controller layers.
/// </summary>
public class ConsentEventRepository : IConsentEventRepository, IConsentEventSink
{
    public const string ConsentEventsContainerName = "ConsentEvents";

    private readonly Container _container;

    public ConsentEventRepository(CosmosClient cosmosClient, string databaseName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(ConsentEventsContainerName);
    }

    public async Task AppendAsync(ConsentEvent evt)
    {
        NormalizeEnvelope(evt);
        try
        {
            await _container.CreateItemAsync(evt, new PartitionKey(evt.PartitionKey));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Idempotent append: duplicate EventId = same append, ignore.
        }
    }

    public async Task<IReadOnlyList<ConsentEvent>> ListByConsentAsync(
        string tenantId, string consentId, CancellationToken ct = default)
    {
        var partitionKey = ConsentEvent.BuildPartitionKey(tenantId, consentId);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.partitionKey = @pk ORDER BY c.occurredAt ASC")
            .WithParameter("@pk", partitionKey);

        var iterator = _container.GetItemQueryIterator<ConsentEvent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        var results = new List<ConsentEvent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    internal static void NormalizeEnvelope(ConsentEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.ConsentId))
            throw new ArgumentException("TenantId and ConsentId are required");

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = ConsentEvent.BuildPartitionKey(evt.TenantId, evt.ConsentId);
    }
}
