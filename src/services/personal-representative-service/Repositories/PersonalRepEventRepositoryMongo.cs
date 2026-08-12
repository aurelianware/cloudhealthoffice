using PersonalRepresentativeService.Models;
using MongoDB.Driver;

namespace PersonalRepresentativeService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IPersonalRepEventRepository"/> and
/// <see cref="IPersonalRepEventSink"/>. Append is idempotent via the unique
/// index on <c>(tenantId, personalRepId, eventId)</c> created at startup
/// by <c>HostedServices.PersonalRepIndexInitializer</c>.
/// </summary>
public class PersonalRepEventRepositoryMongo : IPersonalRepEventRepository, IPersonalRepEventSink
{
    public const string PersonalRepEventsCollectionName = "PersonalRepEvents";

    private readonly IMongoCollection<PersonalRepEvent> _collection;

    public PersonalRepEventRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<PersonalRepEvent>(PersonalRepEventsCollectionName);
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

    public async Task AppendAsync(PersonalRepEvent evt)
    {
        NormalizeEnvelope(evt);
        try
        {
            await _collection.InsertOneAsync(evt);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Duplicate EventId — idempotent replay, drop silently.
        }
    }

    public async Task<IReadOnlyList<PersonalRepEvent>> ListByRepAsync(
        string tenantId, string personalRepId, CancellationToken ct = default)
    {
        var filter = Builders<PersonalRepEvent>.Filter.Eq(e => e.TenantId, tenantId)
                   & Builders<PersonalRepEvent>.Filter.Eq(e => e.PersonalRepId, personalRepId);
        var results = await _collection.Find(filter).ToListAsync(ct);
        return results.OrderBy(e => e.OccurredAt).ToList();
    }
}
