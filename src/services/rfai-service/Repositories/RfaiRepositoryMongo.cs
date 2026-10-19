using MongoDB.Driver;
using RfaiService.Exceptions;
using RfaiService.Models;

namespace RfaiService.Repositories;

public class RfaiRepositoryMongo : IRfaiRepository
{
    private readonly IMongoCollection<RfaiCase> _collection;
    private readonly ILogger<RfaiRepositoryMongo> _logger;

    public RfaiRepositoryMongo(IMongoDatabase database, ILogger<RfaiRepositoryMongo> logger)
    {
        _collection = database.GetCollection<RfaiCase>("RfaiCases");
        _logger = logger;

        // Compound indexes: (tenantId, authNumber) for auth-based lookups,
        // (tenantId, status) for open-case queries, (tenantId, createdAt) for history.
        var keys = Builders<RfaiCase>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Ascending(r => r.AuthNumber)),
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Ascending(r => r.Status)),
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Descending(r => r.CreatedAt)),
            // The provider-facing handle a CDex submission correlates on. Unique
            // within a tenant so two cases can never answer to the same
            // tracking id and make a submission ambiguous.
            new CreateIndexModel<RfaiCase>(
                keys.Ascending(r => r.TenantId).Ascending(r => r.TrackingId),
                new CreateIndexOptions { Unique = true, Sparse = true }),
        });
    }

    public async Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId));

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId),
            Builders<RfaiCase>.Filter.Eq(r => r.AuthNumber, authNumber));

        return await _collection
            .Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId),
            Builders<RfaiCase>.Filter.Eq(r => r.TrackingId, trackingId));

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.Revision = 0;
        await _collection.InsertOneAsync(rfaiCase);
        return rfaiCase;
    }

    /// <inheritdoc />
    public async Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase)
    {
        rfaiCase.Revision = 0;
        try
        {
            await _collection.InsertOneAsync(rfaiCase);
            return (rfaiCase, true);
        }
        catch (MongoWriteException ex)
            when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The race resolved the other way: someone already created the case
            // this event addresses. Read theirs rather than creating a second.
            var existing = await GetByIdAsync(rfaiCase.TenantId, rfaiCase.Id);
            if (existing is not null)
                return (existing, false);

            // A duplicate on the tracking-id index rather than the id: vanishingly
            // unlikely (128 bits of randomness), but it must not silently create.
            _logger.LogWarning(
                "RFAI create conflicted on a secondary key for case {Id}", rfaiCase.Id);
            throw;
        }
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<RfaiCase> RevisionIs(long expected)
    {
        var b = Builders<RfaiCase>.Filter;
        return expected == 0
            ? b.Or(b.Eq(r => r.Revision, 0L), b.Exists(r => r.Revision, false))
            : b.Eq(r => r.Revision, expected);
    }

    public async Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.UpdatedAt = DateTime.UtcNow;

        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, rfaiCase.Id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, rfaiCase.TenantId));

        // Conditional on the revision the caller read: a concurrent write in
        // between (a delivery callback, a response intake, a close) makes this
        // one fail instead of silently dropping the other's change.
        var expected = rfaiCase.Revision;
        rfaiCase.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(
                filter & RevisionIs(expected), rfaiCase, new ReplaceOptions { IsUpsert = false });
        }
        catch
        {
            rfaiCase.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            rfaiCase.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new RfaiConcurrencyException(rfaiCase.Id, expected);
            // Missing: unchanged behaviour (nothing written, no error).
        }

        return rfaiCase;
    }
}
