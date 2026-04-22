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

    public async Task AppendAsync(PersonalRepEvent evt)
    {
        PersonalRepEventRepository.NormalizeEnvelope(evt);
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
