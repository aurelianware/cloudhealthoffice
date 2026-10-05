using Microsoft.Azure.Cosmos;
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

public sealed class CosmosProviderBankAccountRepository : IProviderBankAccountRepository
{
    public const string DefaultContainerName = "ProviderBankAccounts";

    private readonly Container _container;

    public CosmosProviderBankAccountRepository(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:ProviderBankAccountsContainer"] ?? DefaultContainerName;
        _container = client.GetContainer(databaseName, containerName);
    }

    public async Task<ProviderBankAccountRecord?> GetAsync(string tenantId, string providerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<ProviderBankAccountRecord>(
                ProviderBankAccountRecord.KeyFor(tenantId, providerId), new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        record.Id = ProviderBankAccountRecord.KeyFor(record.TenantId, record.ProviderId);
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
        // document is unchanged since (If-Match), so the revision check and
        // the write are one conditional operation.
        ItemResponse<ProviderBankAccountRecord> current;
        try
        {
            current = await _container.ReadItemAsync<ProviderBankAccountRecord>(record.Id, partition, cancellationToken: ct);
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
