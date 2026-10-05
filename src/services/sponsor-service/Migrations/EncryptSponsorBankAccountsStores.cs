using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;
using SponsorService.Models;
using SponsorService.Repositories;

namespace SponsorService.Migrations;

/// <summary>
/// A sponsor document as stored (billing account number not decrypted), with
/// what a conditional write needs: the value read (Mongo) or the ETag (Cosmos).
/// </summary>
public sealed record StoredSponsor(Sponsor Sponsor, string? ETag);

/// <summary>Sponsor documents that carry a billing account number, read and written as stored.</summary>
public interface ISponsorRowStore
{
    /// <summary>Sponsors of <paramref name="tenant"/> (every tenant when null) with a non-empty billing account number.</summary>
    IAsyncEnumerable<StoredSponsor> ListWithBillingAccountAsync(string? tenant, CancellationToken ct);

    /// <summary>
    /// Sets <c>BillingInfo.BillingAccountNumber</c> to <paramref name="stored"/>
    /// only if the document is unchanged since it was listed. False on a conflict.
    /// </summary>
    Task<bool> SetBillingAccountIfUnchangedAsync(StoredSponsor row, string stored, CancellationToken ct);
}

/// <summary><c>SponsorBankAccounts</c> records, read and saved as stored (no decryption).</summary>
public interface ISponsorBankAccountRecordStore
{
    IAsyncEnumerable<SponsorBankAccountRecord> ListAsync(string? tenant, CancellationToken ct);

    /// <summary>The service's own revision-checked save. False on a conflict.</summary>
    Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct);
}

/// <summary>Sponsor documents in Mongo (the collection SponsorRepositoryMongo uses).</summary>
public sealed class MongoSponsorRowStore : ISponsorRowStore
{
    private readonly IMongoCollection<Sponsor> _collection;

    public MongoSponsorRowStore(IMongoDatabase database, IConfiguration configuration)
        => _collection = database.GetCollection<Sponsor>(configuration["CosmosDb:ContainerName"] ?? "Sponsors");

    public async IAsyncEnumerable<StoredSponsor> ListWithBillingAccountAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        var f = Builders<Sponsor>.Filter;
        var filter = f.And(f.Ne(s => s.BillingInfo!.BillingAccountNumber, null), f.Ne(s => s.BillingInfo!.BillingAccountNumber, string.Empty));
        if (tenant != null) filter &= f.Eq(s => s.TenantId, tenant);
        using var cursor = await _collection.Find(filter).SortBy(s => s.Id).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var sponsor in cursor.Current)
                yield return new StoredSponsor(sponsor, null);
        }
    }

    public async Task<bool> SetBillingAccountIfUnchangedAsync(StoredSponsor row, string stored, CancellationToken ct)
    {
        var f = Builders<Sponsor>.Filter;
        var match = f.And(
            f.Eq(s => s.Id, row.Sponsor.Id),
            f.Eq(s => s.TenantId, row.Sponsor.TenantId),
            f.Eq(s => s.BillingInfo!.BillingAccountNumber, row.Sponsor.BillingInfo!.BillingAccountNumber));
        var result = await _collection.UpdateOneAsync(match,
            Builders<Sponsor>.Update.Set(s => s.BillingInfo!.BillingAccountNumber, stored), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}

/// <summary>Bank-account records in Mongo; saves through <see cref="MongoSponsorBankAccountRepository"/>.</summary>
public sealed class MongoSponsorBankAccountRecordStore : ISponsorBankAccountRecordStore
{
    private readonly IMongoCollection<SponsorBankAccountRecord> _collection;
    private readonly MongoSponsorBankAccountRepository _repository;

    public MongoSponsorBankAccountRecordStore(IMongoDatabase database, IConfiguration configuration)
    {
        _collection = database.GetCollection<SponsorBankAccountRecord>(
            configuration["MongoDb:SponsorBankAccountsCollection"] ?? MongoSponsorBankAccountRepository.DefaultCollectionName);
        _repository = new MongoSponsorBankAccountRepository(database, configuration);
    }

    public async IAsyncEnumerable<SponsorBankAccountRecord> ListAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = tenant == null
            ? Builders<SponsorBankAccountRecord>.Filter.Empty
            : Builders<SponsorBankAccountRecord>.Filter.Eq(r => r.TenantId, tenant);
        using var cursor = await _collection.Find(filter).SortBy(r => r.Id).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var record in cursor.Current)
                yield return record;
        }
    }

    public Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct)
        => _repository.SaveAsync(record, expectedRevision, ct);
}

/// <summary>Sponsor documents in the Cosmos <c>Sponsors</c> container (partition key: the tenant id).</summary>
public sealed class CosmosSponsorRowStore : ISponsorRowStore
{
    private readonly Container _container;

    public CosmosSponsorRowStore(CosmosClient client, IConfiguration configuration)
        => _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", "Sponsors");

    public async IAsyncEnumerable<StoredSponsor> ListWithBillingAccountAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = tenant == null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(tenant) };
        using var iterator = _container.GetItemQueryIterator<Sponsor>(new QueryDefinition("SELECT * FROM c"), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var listed in await iterator.ReadNextAsync(ct))
            {
                if (string.IsNullOrEmpty(listed.BillingInfo?.BillingAccountNumber)) continue;
                if (tenant != null && !string.Equals(listed.TenantId, tenant, StringComparison.Ordinal)) continue;
                // Re-read for the ETag the conditional patch needs.
                ItemResponse<Sponsor> current;
                try
                {
                    current = await _container.ReadItemAsync<Sponsor>(listed.Id, new PartitionKey(listed.TenantId), cancellationToken: ct);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    continue;
                }
                yield return new StoredSponsor(current.Resource, current.ETag);
            }
        }
    }

    public async Task<bool> SetBillingAccountIfUnchangedAsync(StoredSponsor row, string stored, CancellationToken ct)
    {
        try
        {
            // Only the one field changes; the rest of the document is left as it is.
            await _container.PatchItemAsync<Sponsor>(row.Sponsor.Id, new PartitionKey(row.Sponsor.TenantId),
                new[] { PatchOperation.Set("/billingInfo/billingAccountNumber", stored) },
                new PatchItemRequestOptions { IfMatchEtag = row.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}

/// <summary>Bank-account records in the Cosmos <c>SponsorBankAccounts</c> container.</summary>
public sealed class CosmosSponsorBankAccountRecordStore : ISponsorBankAccountRecordStore
{
    private readonly Container _container;
    private readonly CosmosSponsorBankAccountRepository _repository;

    public CosmosSponsorBankAccountRecordStore(CosmosClient client, IConfiguration configuration)
    {
        _container = client.GetContainer(
            configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
            configuration["CosmosDb:SponsorBankAccountsContainer"] ?? CosmosSponsorBankAccountRepository.DefaultContainerName);
        _repository = new CosmosSponsorBankAccountRepository(client, configuration);
    }

    public async IAsyncEnumerable<SponsorBankAccountRecord> ListAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = tenant == null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(tenant) };
        using var iterator = _container.GetItemQueryIterator<SponsorBankAccountRecord>(new QueryDefinition("SELECT * FROM c"), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var record in await iterator.ReadNextAsync(ct))
            {
                if (tenant == null || string.Equals(record.TenantId, tenant, StringComparison.Ordinal)) yield return record;
            }
        }
    }

    /// <summary>The service's own revision-checked save (ETag-conditional replace).</summary>
    public Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct)
        => _repository.SaveAsync(record, expectedRevision, ct);
}
