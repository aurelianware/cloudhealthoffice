using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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

    /// <summary>Marks the document in the shared collection (leases live beside the invoices they fence).</summary>
    public string DocType { get; set; } = DocumentType;

    /// <summary>Not "groupNumber": the invoice queries by group must never match a lease.</summary>
    public string LeaseGroupNumber { get; set; } = string.Empty;

    public string LeaseOwner { get; set; } = string.Empty;

    public DateTime AcquiredAt { get; set; }

    public DateTime ExpiresAt { get; set; }

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

    /// <summary>One lease per tenant and group; the Mongo _id.</summary>
    public static string IdFor(string tenantId, string groupNumber) =>
        IdPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{groupNumber}")))[..32].ToLowerInvariant();
}

/// <summary>A held lease: which group, and who holds it.</summary>
public sealed class BillingLeaseHandle
{
    public string GroupNumber { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
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
