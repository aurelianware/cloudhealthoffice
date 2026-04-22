using ConsentService.Models;
using MongoDB.Driver;

namespace ConsentService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IConsentEventRepository"/> and
/// <see cref="IConsentEventSink"/>. Append is idempotent via the unique
/// index on <c>(tenantId, consentId, eventId)</c> created at startup by
/// <c>HostedServices.ConsentIndexInitializer</c>.
/// </summary>
public class ConsentEventRepositoryMongo : IConsentEventRepository, IConsentEventSink
{
    public const string ConsentEventsCollectionName = "ConsentEvents";

    private readonly IMongoCollection<ConsentEvent> _collection;

    public ConsentEventRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<ConsentEvent>(ConsentEventsCollectionName);
    }

    public async Task AppendAsync(ConsentEvent evt)
    {
        ConsentEventRepository.NormalizeEnvelope(evt);
        try
        {
            await _collection.InsertOneAsync(evt);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Duplicate EventId — idempotent replay, drop silently.
        }
    }

    public async Task<IReadOnlyList<ConsentEvent>> ListByConsentAsync(
        string tenantId, string consentId, CancellationToken ct = default)
    {
        var filter = Builders<ConsentEvent>.Filter.Eq(e => e.TenantId, tenantId)
                   & Builders<ConsentEvent>.Filter.Eq(e => e.ConsentId, consentId);
        var results = await _collection.Find(filter).ToListAsync(ct);
        return results.OrderBy(e => e.OccurredAt).ToList();
    }
}
