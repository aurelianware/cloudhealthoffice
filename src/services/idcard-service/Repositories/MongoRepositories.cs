using CloudHealthOffice.Infrastructure.Data;
using IdCardService.Models;
using MongoDB.Driver;

namespace IdCardService.Repositories;

public class MongoIdCardOrderRepository : IIdCardOrderRepository
{
    private readonly IMongoCollection<IdCardOrder> _collection;

    public MongoIdCardOrderRepository(IMongoDatabase db)
    {
        _collection = db.GetCollection<IdCardOrder>("idcard_orders");
    }

    public Task UpsertAsync(IdCardOrder order, CancellationToken ct = default)
    {
        return _collection.ReplaceOneAsync(
            x => x.Id == order.Id && x.TenantId == order.TenantId,
            order,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    public async Task<IdCardOrder?> GetAsync(string tenantId, string orderId, CancellationToken ct = default)
    {
        return await _collection
            .Find(x => x.TenantId == tenantId && x.Id == orderId)
            .FirstOrDefaultAsync(ct);
    }
}

public class MongoIdCardRecordRepository : IIdCardRecordRepository
{
    private readonly IMongoCollection<IdCardRecord> _collection;

    public MongoIdCardRecordRepository(IMongoDatabase db)
    {
        _collection = db.GetCollection<IdCardRecord>("idcard_records");
        _collection.Indexes.CreateOne(new CreateIndexModel<IdCardRecord>(
            Builders<IdCardRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.CardId),
            new CreateIndexOptions { Unique = true, Name = "ix_tenant_card" }));
        _collection.Indexes.CreateOne(new CreateIndexModel<IdCardRecord>(
            Builders<IdCardRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.MemberId),
            new CreateIndexOptions { Name = "ix_tenant_member" }));
    }

    public async Task UpsertAsync(IdCardRecord record, CancellationToken ct = default)
    {
        // Conditional on the revision read (0 also matches cards written before
        // the field existed). A missing card is inserted as before; a card whose
        // revision moved on does not match, so the upsert tries to insert a
        // second (TenantId, CardId) and the unique index refuses it.
        var f = Builders<IdCardRecord>.Filter;
        var expected = record.Revision;
        var revisionFilter = expected == 0
            ? f.Or(f.Eq(x => x.Revision, 0L), f.Exists(x => x.Revision, false))
            : f.Eq(x => x.Revision, expected);
        var filter = f.Eq(x => x.TenantId, record.TenantId) & f.Eq(x => x.CardId, record.CardId) & revisionFilter;

        record.Revision = expected + 1;
        try
        {
            await _collection.ReplaceOneAsync(filter, record, new ReplaceOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            record.Revision = expected;
            throw new IdCardConcurrencyException(record.CardId, expected, ex);
        }
        catch
        {
            record.Revision = expected;
            throw;
        }
    }

    public async Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default)
    {
        return await _collection
            .Find(x => x.TenantId == tenantId && x.CardId == cardId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        return await _collection
            .Find(x => x.TenantId == tenantId && x.MemberId == memberId)
            .SortByDescending(x => x.IssuedAt)
            .ToListAsync(ct);
    }

    [CrossTenantQuery("QnxtMirrorReconciliationJob (hosted service): re-enqueues each record with its own TenantId.")]
    public async Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default)
    {
        return await _collection.Find(x => x.IssuedAt >= since).ToListAsync(ct);
    }
}

public class MongoIdCardTemplateRepository : IIdCardTemplateRepository
{
    private readonly IMongoCollection<IdCardTemplate> _collection;

    public MongoIdCardTemplateRepository(IMongoDatabase db)
    {
        _collection = db.GetCollection<IdCardTemplate>("idcard_templates");
        // Compound unique index: template ids only have to be unique within
        // a tenant. Without the tenant in the filter/index, two tenants
        // seeding the same well-known id (e.g. "global-default") would
        // overwrite each other on upsert.
        _collection.Indexes.CreateOne(new CreateIndexModel<IdCardTemplate>(
            Builders<IdCardTemplate>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Id),
            new CreateIndexOptions { Unique = true, Name = "ix_tenant_id" }));
    }

    public Task UpsertAsync(IdCardTemplate template, CancellationToken ct = default)
    {
        return _collection.ReplaceOneAsync(
            x => x.TenantId == template.TenantId && x.Id == template.Id,
            template,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    public async Task<IdCardTemplate?> FindBySponsorAndPlanAsync(string tenantId, string sponsorId, string planId, CancellationToken ct = default)
    {
        return await _collection.Find(x =>
            x.TenantId == tenantId && x.SponsorId == sponsorId && x.PlanId == planId).FirstOrDefaultAsync(ct);
    }

    public async Task<IdCardTemplate?> FindSponsorDefaultAsync(string tenantId, string sponsorId, CancellationToken ct = default)
    {
        return await _collection.Find(x =>
            x.TenantId == tenantId && x.SponsorId == sponsorId && x.PlanId == null).FirstOrDefaultAsync(ct);
    }

    public async Task<IdCardTemplate?> FindGlobalDefaultAsync(string tenantId, CancellationToken ct = default)
    {
        return await _collection.Find(x =>
            x.TenantId == tenantId && x.IsGlobalDefault).FirstOrDefaultAsync(ct);
    }
}
