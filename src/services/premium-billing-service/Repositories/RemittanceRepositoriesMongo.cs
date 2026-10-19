using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public sealed class RemittanceBatchRepositoryMongo : IRemittanceBatchRepository
{
    private readonly IMongoCollection<RemittanceBatch> _collection;
    private readonly IHttpContextAccessor _http;

    public RemittanceBatchRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<RemittanceBatch>("RemittanceBatches");
        _http = http;
    }

    private FilterDefinition<RemittanceBatch> Tenant() =>
        Builders<RemittanceBatch>.Filter.Eq(x => x.TenantId, RepositoryTenant.From(_http));

    public async Task<RemittanceBatch?> GetByIdAsync(string id) =>
        await _collection.Find(Builders<RemittanceBatch>.Filter.And(Tenant(), Builders<RemittanceBatch>.Filter.Eq(x => x.Id, id)))
            .FirstOrDefaultAsync();

    public async Task<bool> TryCreateAsync(RemittanceBatch batch)
    {
        batch.TenantId = RepositoryTenant.From(_http);
        batch.Revision = 0;
        try
        {
            await _collection.InsertOneAsync(batch);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <summary>Matches the revision a write was based on (batches saved before revisions existed count as 0).</summary>
    private static FilterDefinition<RemittanceBatch> RevisionIs(long expected)
    {
        var b = Builders<RemittanceBatch>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    /// <summary>
    /// Replaces the batch only if it is still at the revision it was read at;
    /// <see cref="RemittanceBatchConcurrencyException"/> when another write came
    /// first. A batch that does not exist is not written (as before).
    /// </summary>
    public async Task<RemittanceBatch> UpdateAsync(RemittanceBatch batch)
    {
        var filter = Builders<RemittanceBatch>.Filter.And(
            Builders<RemittanceBatch>.Filter.Eq(x => x.Id, batch.Id),
            Builders<RemittanceBatch>.Filter.Eq(x => x.TenantId, batch.TenantId));
        var expected = batch.Revision;
        batch.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), batch);
        }
        catch
        {
            batch.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            batch.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new RemittanceBatchConcurrencyException(batch.Id, expected);
        }
        return batch;
    }

    public async Task<IEnumerable<RemittanceBatch>> FindByCrossSourceKeyAsync(string crossSourceKey) =>
        await _collection.Find(Builders<RemittanceBatch>.Filter.And(Tenant(),
            Builders<RemittanceBatch>.Filter.Eq(x => x.CrossSourceKey, crossSourceKey))).ToListAsync();

    public async Task<IEnumerable<RemittanceBatch>> FindLockboxByPaymentDateAsync(DateTime paymentDate)
    {
        // BAI2 supplies a business date, not a local-time instant. Do not let
        // the host time zone move the Mongo query's day boundaries.
        var day = DateTime.SpecifyKind(paymentDate.Date, DateTimeKind.Utc);
        var f = Builders<RemittanceBatch>.Filter;
        return await _collection.Find(f.And(Tenant(),
            f.Eq(x => x.Source, RemittanceSource.Lockbox),
            f.Gte(x => x.PaymentDate, day), f.Lt(x => x.PaymentDate, day.AddDays(1)),
            f.Ne(x => x.Status, RemittanceBatchStatus.NotPosted))).ToListAsync();
    }

    public async Task<IEnumerable<RemittanceBatch>> SearchAsync(DateTime? receivedFrom = null, DateTime? receivedTo = null, int page = 1, int pageSize = 50)
    {
        var f = Builders<RemittanceBatch>.Filter;
        var filters = new List<FilterDefinition<RemittanceBatch>> { Tenant() };
        if (receivedFrom.HasValue) filters.Add(f.Gte(x => x.ReceivedAt, receivedFrom.Value));
        if (receivedTo.HasValue) filters.Add(f.Lte(x => x.ReceivedAt, receivedTo.Value));
        return await _collection.Find(f.And(filters)).SortByDescending(x => x.ReceivedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Limit(pageSize).ToListAsync();
    }
}

public sealed class RemittanceExceptionRepositoryMongo : IRemittanceExceptionRepository
{
    private readonly IMongoCollection<RemittanceException> _collection;
    private readonly IHttpContextAccessor _http;

    public RemittanceExceptionRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<RemittanceException>("RemittanceExceptions");
        _http = http;
    }

    private FilterDefinition<RemittanceException> Tenant() =>
        Builders<RemittanceException>.Filter.Eq(x => x.TenantId, RepositoryTenant.From(_http));

    public async Task<RemittanceException?> GetByIdAsync(string id) =>
        await _collection.Find(Builders<RemittanceException>.Filter.And(Tenant(), Builders<RemittanceException>.Filter.Eq(x => x.Id, id)))
            .FirstOrDefaultAsync();

    public async Task<IEnumerable<RemittanceException>> ListAsync(RemittanceExceptionStatus? status = null, int page = 1, int pageSize = 50)
    {
        var f = Builders<RemittanceException>.Filter;
        var filter = status.HasValue ? f.And(Tenant(), f.Eq(x => x.Status, status.Value)) : Tenant();
        return await _collection.Find(filter).SortBy(x => x.CreatedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize).Limit(pageSize).ToListAsync();
    }

    public async Task<RemittanceException> CreateAsync(RemittanceException exception)
    {
        exception.TenantId = RepositoryTenant.From(_http);
        try
        {
            await _collection.InsertOneAsync(exception);
            return exception;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return (await GetByIdAsync(exception.Id))!;
        }
    }

    public async Task<bool> TryTransitionAsync(string id, RemittanceExceptionStatus from, RemittanceExceptionStatus to)
    {
        var f = Builders<RemittanceException>.Filter;
        var result = await _collection.UpdateOneAsync(
            f.And(Tenant(), f.Eq(x => x.Id, id), f.Eq(x => x.Status, from)),
            Builders<RemittanceException>.Update.Set(x => x.Status, to));
        return result.ModifiedCount == 1;
    }

    public async Task<RemittanceException> UpdateAsync(RemittanceException exception)
    {
        var filter = Builders<RemittanceException>.Filter.And(
            Builders<RemittanceException>.Filter.Eq(x => x.Id, exception.Id),
            Builders<RemittanceException>.Filter.Eq(x => x.TenantId, exception.TenantId));
        await _collection.ReplaceOneAsync(filter, exception);
        return exception;
    }
}

public sealed class SponsorAccountRepositoryMongo : ISponsorAccountRepository
{
    private readonly IMongoCollection<SponsorAccount> _collection;
    private readonly IHttpContextAccessor _http;

    public SponsorAccountRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<SponsorAccount>("SponsorAccounts");
        _http = http;
    }

    public async Task<SponsorAccount?> GetAsync(string groupNumber)
    {
        var tenant = RepositoryTenant.From(_http);
        return await _collection.Find(Builders<SponsorAccount>.Filter.And(
                Builders<SponsorAccount>.Filter.Eq(x => x.Id, SponsorAccount.IdFor(tenant, groupNumber)),
                Builders<SponsorAccount>.Filter.Eq(x => x.TenantId, tenant)))
            .FirstOrDefaultAsync();
    }

    public async Task<SponsorAccount> UpdateAsync(string groupNumber, Action<SponsorAccount> change)
    {
        var tenant = RepositoryTenant.From(_http);
        var id = SponsorAccount.IdFor(tenant, groupNumber);
        for (var attempt = 0; attempt < RepositoryTenant.MaxConcurrencyAttempts; attempt++)
        {
            var account = await GetAsync(groupNumber);
            if (account == null)
            {
                account = new SponsorAccount { Id = id, TenantId = tenant, GroupNumber = groupNumber, Version = 1 };
                change(account);
                account.LastUpdatedAt = DateTime.UtcNow;
                try
                {
                    await _collection.InsertOneAsync(account);
                    return account;
                }
                catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    continue; // created concurrently: re-read and apply on top
                }
            }

            var expected = account.Version;
            change(account);
            account.Version = expected + 1;
            account.LastUpdatedAt = DateTime.UtcNow;
            var result = await _collection.ReplaceOneAsync(Builders<SponsorAccount>.Filter.And(
                Builders<SponsorAccount>.Filter.Eq(x => x.Id, id),
                Builders<SponsorAccount>.Filter.Eq(x => x.TenantId, tenant),
                Builders<SponsorAccount>.Filter.Eq(x => x.Version, expected)), account);
            if (result.ModifiedCount == 1)
                return account;
        }
        throw new ConcurrencyConflictException($"Sponsor account {groupNumber} kept changing; the update was not saved");
    }
}
