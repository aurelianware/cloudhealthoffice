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
/// Invoice writes are fenced by it (see <see cref="IPremiumInvoiceRepository.CreateFencedAsync"/>):
/// a holder whose lease was taken over cannot write.
/// </summary>
public class BillingLease
{
    public const string DocumentType = "billingLease";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Marks the document in a shared container (Cosmos keeps leases beside the invoices they fence).</summary>
    public string DocType { get; set; } = DocumentType;

    /// <summary>Not "groupNumber": the invoice queries by group must never match a lease.</summary>
    public string LeaseGroupNumber { get; set; } = string.Empty;

    public string LeaseOwner { get; set; } = string.Empty;

    public DateTime AcquiredAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("_etag")]
    [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
    public string? ETag { get; set; }

    /// <summary>Element name of <see cref="DocType"/> in MongoDB (no naming convention there).</summary>
    public const string MongoDocTypeElement = nameof(DocType);

    public const string IdPrefix = "lease-";

    public static bool IsLeaseId(string? id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>Invoice reads and writes never touch a lease document (they share a container/collection).</summary>
    public static void RefuseLeaseId(string? id)
    {
        if (IsLeaseId(id))
            throw new InvalidOperationException($"{id} is not an invoice");
    }

    /// <summary>One lease per tenant and group; safe as a Cosmos id and a Mongo _id.</summary>
    public static string IdFor(string tenantId, string groupNumber) =>
        IdPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{groupNumber}")))[..32].ToLowerInvariant();
}

/// <summary>A held lease: who holds it, and (Cosmos) the version of the lease document it holds.</summary>
public sealed class BillingLeaseHandle
{
    public string GroupNumber { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;

    /// <summary>Cosmos: ETag of the lease document as last written by this holder; the write fence.</summary>
    public string? ETag { get; set; }
}

/// <summary>The lease was taken over (it expired while its holder was still working): stop, write nothing.</summary>
public sealed class BillingLeaseLostException : InvalidOperationException
{
    public BillingLeaseLostException(string groupNumber)
        : base($"The billing lease of group {groupNumber} was lost (it expired and another run took it); nothing was written by this run")
    {
    }
}

public interface IBillingLeaseRepository
{
    /// <summary>Takes the lease when nobody holds it or the holder's lease expired; null when it is held.</summary>
    Task<BillingLeaseHandle?> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration);

    /// <summary>
    /// Extends the lease only while <paramref name="lease"/> still holds it and
    /// it has not expired. False: the lease is lost and the holder must stop.
    /// </summary>
    Task<bool> RenewAsync(BillingLeaseHandle lease, DateTime now, TimeSpan duration);

    /// <summary>Gives the lease back, if its holder still holds it.</summary>
    Task ReleaseAsync(BillingLeaseHandle lease);
}

/// <summary>
/// MongoDB: lease documents live in the <c>PremiumInvoices</c> collection (<c>DocType =
/// billingLease</c>), so a fenced invoice write is a single-collection transaction (the
/// only kind Azure Cosmos DB for MongoDB supports). The unique _id makes the first insert win.
/// </summary>
public sealed class BillingLeaseRepositoryMongo : IBillingLeaseRepository
{
    public const string CollectionName = "PremiumInvoices";

    private readonly IMongoCollection<BillingLease> _collection;
    private readonly IHttpContextAccessor _http;

    public BillingLeaseRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<BillingLease>(CollectionName);
        _http = http;
    }

    public async Task<BillingLeaseHandle?> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var lease = new BillingLease
        {
            Id = BillingLease.IdFor(tenantId, groupNumber), TenantId = tenantId, LeaseGroupNumber = groupNumber,
            LeaseOwner = owner, AcquiredAt = now, ExpiresAt = now + duration
        };
        var handle = new BillingLeaseHandle { GroupNumber = groupNumber, Owner = owner };
        try
        {
            await _collection.InsertOneAsync(lease);
            return handle;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
        }

        // Held: take it over only if it expired (one writer wins the conditional update).
        var f = Builders<BillingLease>.Filter;
        var result = await _collection.UpdateOneAsync(
            f.And(f.Eq(l => l.Id, lease.Id), f.Eq(l => l.TenantId, tenantId), f.Lt(l => l.ExpiresAt, now)),
            Builders<BillingLease>.Update.Set(l => l.LeaseOwner, owner).Set(l => l.AcquiredAt, now).Set(l => l.ExpiresAt, lease.ExpiresAt));
        return result.ModifiedCount == 1 ? handle : null;
    }

    public async Task<bool> RenewAsync(BillingLeaseHandle lease, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var f = Builders<BillingLease>.Filter;
        var result = await _collection.UpdateOneAsync(
            f.And(f.Eq(l => l.Id, BillingLease.IdFor(tenantId, lease.GroupNumber)), f.Eq(l => l.TenantId, tenantId),
                f.Eq(l => l.LeaseOwner, lease.Owner), f.Gt(l => l.ExpiresAt, now)),
            Builders<BillingLease>.Update.Set(l => l.ExpiresAt, now + duration));
        return result.MatchedCount == 1;
    }

    public async Task ReleaseAsync(BillingLeaseHandle lease)
    {
        var tenantId = RepositoryTenant.From(_http);
        var id = BillingLease.IdFor(tenantId, lease.GroupNumber);
        await _collection.DeleteOneAsync(l => l.Id == id && l.TenantId == tenantId && l.LeaseOwner == lease.Owner);
    }
}

/// <summary>
/// Cosmos DB: lease documents live in the <c>PremiumInvoices</c> container (partition
/// <c>/tenantId</c>, <c>docType = billingLease</c>), so an invoice write and the lease's
/// ETag check go in one transactional batch. Create conflict and ETag replace take it.
/// </summary>
public sealed class BillingLeaseRepositoryCosmos : IBillingLeaseRepository
{
    /// <summary>Same container as the invoices the lease fences.</summary>
    public const string ContainerName = "PremiumInvoices";

    private readonly Container _container;
    private readonly IHttpContextAccessor _http;

    public BillingLeaseRepositoryCosmos(CosmosClient client, IConfiguration configuration, IHttpContextAccessor http)
    {
        _container = client.GetContainer(configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice", ContainerName);
        _http = http;
    }

    public async Task<BillingLeaseHandle?> TryAcquireAsync(string groupNumber, string owner, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var key = new PartitionKey(tenantId);
        var lease = new BillingLease
        {
            Id = BillingLease.IdFor(tenantId, groupNumber), TenantId = tenantId, LeaseGroupNumber = groupNumber,
            LeaseOwner = owner, AcquiredAt = now, ExpiresAt = now + duration
        };
        try
        {
            var created = await _container.CreateItemAsync(lease, key);
            return new BillingLeaseHandle { GroupNumber = groupNumber, Owner = owner, ETag = created.ETag };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
        }

        try
        {
            var current = await _container.ReadItemAsync<BillingLease>(lease.Id, key);
            if (current.Resource.ExpiresAt >= now)
                return null;
            var replaced = await _container.ReplaceItemAsync(lease, lease.Id, key, new ItemRequestOptions { IfMatchEtag = current.ETag });
            return new BillingLeaseHandle { GroupNumber = groupNumber, Owner = owner, ETag = replaced.ETag };
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            // Someone else took it over, or it was released between our create and read: try again later.
            return null;
        }
    }

    public async Task<bool> RenewAsync(BillingLeaseHandle lease, DateTime now, TimeSpan duration)
    {
        var tenantId = RepositoryTenant.From(_http);
        var key = new PartitionKey(tenantId);
        try
        {
            var current = await _container.ReadItemAsync<BillingLease>(BillingLease.IdFor(tenantId, lease.GroupNumber), key);
            if (current.ETag != lease.ETag || current.Resource.LeaseOwner != lease.Owner || current.Resource.ExpiresAt <= now)
                return false;
            var renewed = current.Resource;
            renewed.ExpiresAt = now + duration;
            var replaced = await _container.ReplaceItemAsync(renewed, renewed.Id, key, new ItemRequestOptions { IfMatchEtag = lease.ETag });
            lease.ETag = replaced.ETag;
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(BillingLeaseHandle lease)
    {
        var tenantId = RepositoryTenant.From(_http);
        var key = new PartitionKey(tenantId);
        try
        {
            var current = await _container.ReadItemAsync<BillingLease>(BillingLease.IdFor(tenantId, lease.GroupNumber), key);
            if (current.Resource.LeaseOwner != lease.Owner)
                return;
            await _container.DeleteItemAsync<BillingLease>(current.Resource.Id, key, new ItemRequestOptions { IfMatchEtag = current.ETag });
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            // No longer ours (taken over) or already gone.
        }
    }
}
