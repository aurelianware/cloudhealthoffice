using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace PremiumBillingService.Repositories;

/// <summary>
/// A short exclusive lease on one sponsor group's rated billing: only its
/// holder reads the group's invoices, computes and writes, so two periods (or
/// two runs) of one group are never reconciled from the same stale ledger.
/// </summary>
public class BillingLease
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string GroupNumber { get; set; } = string.Empty;

    public string Owner { get; set; } = string.Empty;

    public DateTime AcquiredAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("_etag")]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    public string? ETag { get; set; }

    /// <summary>One lease per tenant and group; safe as a Cosmos id and a Mongo _id.</summary>
    public static string IdFor(string tenantId, string groupNumber) =>
        "lease-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{groupNumber}")))[..32].ToLowerInvariant();
}

public interface IBillingLeaseRepository
{
    /// <summary>Takes the lease when nobody holds it or the holder's lease expired.</summary>
    Task<bool> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration);

    /// <summary>Gives the lease back, if <paramref name="owner"/> still holds it.</summary>
    Task ReleaseAsync(string groupNumber, string owner);
}

/// <summary>MongoDB: collection <c>BillingLeases</c>; the unique _id makes the first insert win.</summary>
public sealed class BillingLeaseRepositoryMongo : IBillingLeaseRepository
{
    private readonly IMongoCollection<BillingLease> _collection;
    private readonly IHttpContextAccessor _http;

    public BillingLeaseRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<BillingLease>("BillingLeases");
        _http = http;
    }

    public async Task<bool> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var lease = new BillingLease
        {
            Id = BillingLease.IdFor(tenantId, groupNumber), TenantId = tenantId, GroupNumber = groupNumber,
            Owner = owner, AcquiredAt = now, ExpiresAt = now + duration
        };
        try
        {
            await _collection.InsertOneAsync(lease);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }

        // Held: take it over only if it expired (one writer wins the conditional update).
        var f = Builders<BillingLease>.Filter;
        var result = await _collection.UpdateOneAsync(
            f.And(f.Eq(l => l.Id, lease.Id), f.Eq(l => l.TenantId, tenantId), f.Lt(l => l.ExpiresAt, now)),
            Builders<BillingLease>.Update.Set(l => l.Owner, owner).Set(l => l.AcquiredAt, now).Set(l => l.ExpiresAt, lease.ExpiresAt));
        return result.ModifiedCount == 1;
    }

    public async Task ReleaseAsync(string groupNumber, string owner)
    {
        var tenantId = RepositoryTenant.From(_http);
        await _collection.DeleteOneAsync(l => l.Id == BillingLease.IdFor(tenantId, groupNumber) && l.TenantId == tenantId && l.Owner == owner);
    }
}

/// <summary>Cosmos DB: container <c>BillingLeases</c>, partition key <c>/tenantId</c>; create conflict and ETag replace.</summary>
public sealed class BillingLeaseRepositoryCosmos : IBillingLeaseRepository
{
    public const string ContainerName = "BillingLeases";

    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public BillingLeaseRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", ContainerName);
        _http = http;
    }

    public async Task<bool> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var key = new PartitionKey(tenantId);
        var lease = new BillingLease
        {
            Id = BillingLease.IdFor(tenantId, groupNumber), TenantId = tenantId, GroupNumber = groupNumber,
            Owner = owner, AcquiredAt = now, ExpiresAt = now + duration
        };
        try
        {
            await _container.CreateItemAsync(lease, key);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
        }

        try
        {
            var current = await _container.ReadItemAsync<BillingLease>(lease.Id, key);
            if (current.Resource.ExpiresAt >= now)
                return false;
            await _container.ReplaceItemAsync(lease, lease.Id, key, new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            // Someone else took it over, or it was released between our create and read: try again later.
            return false;
        }
    }

    public async Task ReleaseAsync(string groupNumber, string owner)
    {
        var tenantId = RepositoryTenant.From(_http);
        var key = new PartitionKey(tenantId);
        try
        {
            var current = await _container.ReadItemAsync<BillingLease>(BillingLease.IdFor(tenantId, groupNumber), key);
            if (current.Resource.Owner != owner)
                return;
            await _container.DeleteItemAsync<BillingLease>(current.Resource.Id, key, new ItemRequestOptions { IfMatchEtag = current.ETag });
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
        }
    }
}
