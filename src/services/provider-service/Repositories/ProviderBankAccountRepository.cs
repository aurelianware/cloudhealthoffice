using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Storage for <see cref="ProviderBankAccountRecord"/>: one document per
/// provider per tenant. Saves are conditional on the revision that was read,
/// so two concurrent decisions cannot both apply.
/// </summary>
public interface IProviderBankAccountRepository
{
    Task<ProviderBankAccountRecord?> GetAsync(string tenantId, string providerId, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="record"/> if the stored revision is still
    /// <paramref name="expectedRevision"/> (0: the record must not exist yet),
    /// and increments <see cref="ProviderBankAccountRecord.Revision"/>.
    /// Returns false, writing nothing, when someone else saved first.
    /// </summary>
    Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct = default);
}

public sealed class MongoProviderBankAccountRepository : IProviderBankAccountRepository
{
    public const string DefaultCollectionName = "ProviderBankAccounts";

    private readonly IMongoCollection<ProviderBankAccountRecord> _collection;

    public MongoProviderBankAccountRepository(IMongoDatabase database, IConfiguration configuration)
    {
        var name = configuration["MongoDb:ProviderBankAccountsCollection"] ?? DefaultCollectionName;
        _collection = database.GetCollection<ProviderBankAccountRecord>(name);
    }

    public async Task<ProviderBankAccountRecord?> GetAsync(string tenantId, string providerId, CancellationToken ct = default)
    {
        var b = Builders<ProviderBankAccountRecord>.Filter;
        var filter = b.And(
            b.Eq(r => r.Id, ProviderBankAccountRecord.KeyFor(tenantId, providerId)),
            b.Eq(r => r.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        record.Id = ProviderBankAccountRecord.KeyFor(record.TenantId, record.ProviderId);
        record.Revision = expectedRevision + 1;
        record.UpdatedAt = DateTime.UtcNow;

        if (expectedRevision == 0)
        {
            try
            {
                await _collection.InsertOneAsync(record, cancellationToken: ct);
                return true;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                record.Revision = expectedRevision;
                return false;
            }
        }

        var b = Builders<ProviderBankAccountRecord>.Filter;
        var filter = b.And(
            b.Eq(r => r.Id, record.Id),
            b.Eq(r => r.TenantId, record.TenantId),
            b.Eq(r => r.Revision, expectedRevision));
        var result = await _collection.ReplaceOneAsync(filter, record, cancellationToken: ct);
        if (result.MatchedCount == 1) return true;

        record.Revision = expectedRevision;
        return false;
    }
}
