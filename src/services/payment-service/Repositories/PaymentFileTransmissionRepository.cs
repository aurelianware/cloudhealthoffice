using System.Net;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Transmission records, one per (tenant, file reference). Every call names the
/// tenant (taken from the run, which was read through the tenant-scoped run
/// store). Writes are conditional: an insert only when absent, a replace only
/// while the stored <see cref="PaymentFileTransmission.Version"/> is the one
/// read, so two attempts can never both claim a file.
/// </summary>
public interface IPaymentFileTransmissionRepository
{
    Task<PaymentFileTransmission?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default);

    /// <summary>Inserts the record (setting its id and first version). False when one already exists.</summary>
    Task<bool> TryInsertAsync(PaymentFileTransmission record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the record only while its stored version is <paramref name="expectedVersion"/>;
    /// sets a new version on success. False when it changed (another attempt won) or is gone.
    /// </summary>
    Task<bool> TryReplaceAsync(PaymentFileTransmission record, string expectedVersion, CancellationToken cancellationToken = default);
}

internal static class TransmissionVersions
{
    public static string Next() => Guid.NewGuid().ToString("N");
}

public sealed class PaymentFileTransmissionRepositoryMongo : IPaymentFileTransmissionRepository
{
    public const string CollectionName = "PaymentFileTransmissions";

    private readonly IMongoCollection<PaymentFileTransmission> _collection;

    public PaymentFileTransmissionRepositoryMongo(IMongoDatabase database)
        => _collection = database.GetCollection<PaymentFileTransmission>(CollectionName);

    private static FilterDefinition<PaymentFileTransmission> Key(string tenantId, string fileReference)
        => Builders<PaymentFileTransmission>.Filter.And(
            Builders<PaymentFileTransmission>.Filter.Eq(x => x.Id, PaymentFileTransmission.KeyFor(tenantId, fileReference)),
            Builders<PaymentFileTransmission>.Filter.Eq(x => x.TenantId, tenantId));

    public async Task<PaymentFileTransmission?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
        => await _collection.Find(Key(tenantId, fileReference)).FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> TryInsertAsync(PaymentFileTransmission record, CancellationToken cancellationToken = default)
    {
        record.Id = PaymentFileTransmission.KeyFor(record.TenantId, record.FileReference);
        record.Version = TransmissionVersions.Next();
        try
        {
            await _collection.InsertOneAsync(record, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceAsync(PaymentFileTransmission record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var filter = Builders<PaymentFileTransmission>.Filter.And(
            Key(record.TenantId, record.FileReference),
            Builders<PaymentFileTransmission>.Filter.Eq(x => x.Version, expectedVersion));
        var previous = record.Version;
        record.Version = TransmissionVersions.Next();
        var result = await _collection.ReplaceOneAsync(filter, record, cancellationToken: cancellationToken);
        if (result.MatchedCount == 1)
            return true;
        record.Version = previous;
        return false;
    }
}

/// <summary>
/// Cosmos: container <c>PaymentFileTransmissions</c>, partition key
/// <c>/tenantId</c>, created if absent. The replace is pinned with If-Match to
/// the ETag of the read that compared the version.
/// </summary>
public sealed class PaymentFileTransmissionRepositoryCosmos : IPaymentFileTransmissionRepository
{
    public const string ContainerName = "PaymentFileTransmissions";

    private readonly Lazy<Task<Container>> _container;

    public PaymentFileTransmissionRepositoryCosmos(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = new Lazy<Task<Container>>(async () =>
        {
            var response = await client.GetDatabase(databaseName)
                .CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/tenantId"));
            return response.Container;
        });
    }

    public async Task<PaymentFileTransmission?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
    {
        var container = await _container.Value;
        try
        {
            var response = await container.ReadItemAsync<PaymentFileTransmission>(
                PaymentFileTransmission.KeyFor(tenantId, fileReference), new PartitionKey(tenantId), cancellationToken: cancellationToken);
            return response.Resource.TenantId == tenantId ? response.Resource : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> TryInsertAsync(PaymentFileTransmission record, CancellationToken cancellationToken = default)
    {
        record.Id = PaymentFileTransmission.KeyFor(record.TenantId, record.FileReference);
        record.Version = TransmissionVersions.Next();
        var container = await _container.Value;
        try
        {
            await container.CreateItemAsync(record, new PartitionKey(record.TenantId), cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceAsync(PaymentFileTransmission record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var container = await _container.Value;
        var previous = record.Version;
        try
        {
            var current = await container.ReadItemAsync<PaymentFileTransmission>(
                record.Id, new PartitionKey(record.TenantId), cancellationToken: cancellationToken);
            if (current.Resource.Version != expectedVersion)
                return false;
            record.Version = TransmissionVersions.Next();
            await container.ReplaceItemAsync(record, record.Id, new PartitionKey(record.TenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            record.Version = previous;
            return false;
        }
    }
}
