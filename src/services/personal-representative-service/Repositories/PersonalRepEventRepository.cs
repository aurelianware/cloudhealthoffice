using System.Net;
using PersonalRepresentativeService.Models;
using Microsoft.Azure.Cosmos;

namespace PersonalRepresentativeService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IPersonalRepEventRepository"/>
/// and <see cref="IPersonalRepEventSink"/>. Partition key:
/// <c>/partitionKey</c> (<c>{tenantId}:{personalRepId}</c>) so the full
/// audit trail for a rep lives in a single partition and scans cheaply.
///
/// Writes are idempotent: the Cosmos document id is
/// <see cref="PersonalRepEvent.EventId"/>, so a duplicate append with the
/// same EventId returns a 409 that we treat as a no-op — safe under retry
/// from Kafka / controller layers.
/// </summary>
public class PersonalRepEventRepository : IPersonalRepEventRepository, IPersonalRepEventSink
{
    public const string PersonalRepEventsContainerName = "PersonalRepEvents";

    private readonly Container _container;

    public PersonalRepEventRepository(CosmosClient cosmosClient, string databaseName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(PersonalRepEventsContainerName);
    }

    public async Task AppendAsync(PersonalRepEvent evt)
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

    public async Task<IReadOnlyList<PersonalRepEvent>> ListByRepAsync(
        string tenantId, string personalRepId, CancellationToken ct = default)
    {
        var partitionKey = PersonalRepEvent.BuildPartitionKey(tenantId, personalRepId);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.partitionKey = @pk ORDER BY c.occurredAt ASC")
            .WithParameter("@pk", partitionKey);

        var iterator = _container.GetItemQueryIterator<PersonalRepEvent>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });

        var results = new List<PersonalRepEvent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    internal static void NormalizeEnvelope(PersonalRepEvent evt)
    {
        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.PersonalRepId))
            throw new ArgumentException("TenantId and PersonalRepId are required");

        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = evt.EventId;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = PersonalRepEvent.BuildPartitionKey(evt.TenantId, evt.PersonalRepId);
    }
}
