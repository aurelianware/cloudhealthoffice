using Microsoft.Azure.Cosmos;
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

public sealed class CosmosSponsorBankAccountRepository : ISponsorBankAccountRepository
{
    public const string DefaultContainerName = "SponsorBankAccounts";

    private readonly Container _container;

    public CosmosSponsorBankAccountRepository(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:SponsorBankAccountsContainer"] ?? DefaultContainerName;
        _container = client.GetContainer(databaseName, containerName);
    }

    public async Task<SponsorBankAccountRecord?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<SponsorBankAccountRecord>(
                SponsorBankAccountRecord.KeyFor(tenantId, groupNumber), new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        record.Id = SponsorBankAccountRecord.KeyFor(record.TenantId, record.GroupNumber);
        var partition = new PartitionKey(record.TenantId);

        if (expectedRevision == 0)
        {
            record.Revision = 1;
            record.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _container.CreateItemAsync(record, partition, cancellationToken: ct);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                record.Revision = expectedRevision;
                return false;
            }
        }

        // Read the stored revision and its ETag, then replace only if the
        // document is unchanged since (If-Match): the revision check and the
        // write are one conditional operation.
        ItemResponse<SponsorBankAccountRecord> current;
        try
        {
            current = await _container.ReadItemAsync<SponsorBankAccountRecord>(record.Id, partition, cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        if (current.Resource.Revision != expectedRevision) return false;

        record.Revision = expectedRevision + 1;
        record.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _container.ReplaceItemAsync(record, record.Id, partition,
                new ItemRequestOptions { IfMatchEtag = current.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            record.Revision = expectedRevision;
            return false;
        }
    }
}
