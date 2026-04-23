using AppealsService.Models;
using MongoDB.Driver;

namespace AppealsService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IAppealEventRepository"/> and
/// <see cref="IAppealEventSink"/>. Append is idempotent via the unique
/// index on <c>(tenantId, appealId, eventId)</c> created at startup by
/// <c>HostedServices.AppealIndexInitializer</c>.
/// </summary>
public sealed class AppealEventRepositoryMongo : IAppealEventRepository, IAppealEventSink
{
    public const string AppealEventsCollectionName = "AppealEvents";

    private readonly IMongoCollection<AppealEvent> _collection;

    public AppealEventRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<AppealEvent>(AppealEventsCollectionName);
    }

    public async Task AppendAsync(AppealEvent evt, CancellationToken ct = default)
    {
        AppealEventRepository.NormalizeEnvelope(evt);
        try
        {
            await _collection.InsertOneAsync(evt, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Duplicate EventId — idempotent replay, drop silently.
        }
    }

    public async Task<IReadOnlyList<AppealEvent>> ListByAppealAsync(
        string tenantId, string appealId, CancellationToken ct = default)
    {
        var filter = Builders<AppealEvent>.Filter.Eq(e => e.TenantId, tenantId)
                   & Builders<AppealEvent>.Filter.Eq(e => e.AppealId, appealId);
        var results = await _collection.Find(filter).ToListAsync(ct);
        return results.OrderBy(e => e.OccurredAt).ToList();
    }
}
