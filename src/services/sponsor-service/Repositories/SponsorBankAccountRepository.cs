using MongoDB.Driver;
using SponsorService.Models;

namespace SponsorService.Repositories;

/// <summary>
/// Storage for <see cref="SponsorBankAccountRecord"/>: one document per
/// sponsor per tenant, stored as given (numbers already encrypted by
/// SponsorBankAccountService). Saves are conditional on the revision that was
/// read, so two concurrent decisions cannot both apply.
/// </summary>
public interface ISponsorBankAccountRepository
{
    Task<SponsorBankAccountRecord?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="record"/> if the stored revision is still
    /// <paramref name="expectedRevision"/> (0: the record must not exist yet),
    /// and increments <see cref="SponsorBankAccountRecord.Revision"/>.
    /// Returns false, writing nothing, when someone else saved first.
    /// </summary>
    Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct = default);
}

public sealed class MongoSponsorBankAccountRepository : ISponsorBankAccountRepository
{
    public const string DefaultCollectionName = "SponsorBankAccounts";

    private readonly IMongoCollection<SponsorBankAccountRecord> _collection;

    public MongoSponsorBankAccountRepository(IMongoDatabase database, IConfiguration configuration)
    {
        var name = configuration["MongoDb:SponsorBankAccountsCollection"] ?? DefaultCollectionName;
        _collection = database.GetCollection<SponsorBankAccountRecord>(name);
    }

    public async Task<SponsorBankAccountRecord?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var b = Builders<SponsorBankAccountRecord>.Filter;
        var filter = b.And(
            b.Eq(r => r.Id, SponsorBankAccountRecord.KeyFor(tenantId, groupNumber)),
            b.Eq(r => r.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        record.Id = SponsorBankAccountRecord.KeyFor(record.TenantId, record.GroupNumber);
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

        var b = Builders<SponsorBankAccountRecord>.Filter;
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
