using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public class PremiumInvoiceRepositoryMongo : IPremiumInvoiceRepository
{
    private readonly IMongoCollection<PremiumInvoice> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PremiumInvoiceRepositoryMongo> _logger;

    public PremiumInvoiceRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PremiumInvoiceRepositoryMongo> logger)
    {
        _collection = database.GetCollection<PremiumInvoice>("PremiumInvoices");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<PremiumInvoice?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.Id, id),
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PremiumInvoice>.Filter.Eq(x => x.GroupNumber, groupNumber));
        return await _collection.Find(filter).SortByDescending(x => x.BillingPeriodStart).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string invoiceNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PremiumInvoice>.Filter.Eq(x => x.InvoiceNumber, invoiceNumber));
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PremiumInvoice>.Filter.Eq(x => x.BillingPeriodStart, billingPeriodStart));
        return await _collection.Find(filter).SortBy(x => x.GroupNumber).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PremiumInvoice>.Filter.Eq(x => x.Status, status));
        return await _collection.Find(filter).SortBy(x => x.DueDate).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<PremiumInvoice>>
        {
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(groupNumber))
            filters.Add(Builders<PremiumInvoice>.Filter.Eq(x => x.GroupNumber, groupNumber));
        if (periodFrom.HasValue)
            filters.Add(Builders<PremiumInvoice>.Filter.Gte(x => x.BillingPeriodStart, periodFrom.Value));
        if (periodTo.HasValue)
            filters.Add(Builders<PremiumInvoice>.Filter.Lte(x => x.BillingPeriodStart, periodTo.Value));
        if (status.HasValue)
            filters.Add(Builders<PremiumInvoice>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<PremiumInvoice>.Filter.And(filters))
            .SortByDescending(x => x.BillingPeriodStart)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> GetOverdueAsync()
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PremiumInvoice>.Filter.Gt(x => x.BalanceDue, 0m),
            Builders<PremiumInvoice>.Filter.Lt(x => x.DueDate, now),
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.Voided),
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.WriteOff),
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.Paid),
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.Draft));
        return await _collection.Find(filter).SortBy(x => x.DueDate).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
            // A draft is not a bill: leave it out before the last-N cut.
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.Draft),
            Builders<PremiumInvoice>.Filter.ElemMatch(
                x => x.LineItems,
                li => li.MemberId == memberId));
        return await _collection.Find(filter)
            .SortByDescending(x => x.BillingPeriodStart)
            .Limit(take)
            .ToListAsync();
    }

    public async Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice)
    {
        invoice.TenantId = GetTenantId();
        invoice.CreatedAt = DateTime.UtcNow;
        invoice.LastUpdatedAt = DateTime.UtcNow;
        try
        {
            await _collection.InsertOneAsync(invoice);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InvoiceAlreadyExistsException(invoice.Id);
        }
        _logger.LogInformation("Created premium invoice {InvoiceNumber} for group {GroupNumber}",
            invoice.InvoiceNumber, invoice.GroupNumber);
        return invoice;
    }

    public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
    {
        var f = Builders<PremiumInvoice>.Filter;
        var expected = invoice.Version;
        // Invoices saved before versioning have no Version field: version 0 matches them too.
        var versionFilter = expected == 0
            ? f.Or(f.Eq(x => x.Version, 0L), f.Exists(x => x.Version, false))
            : f.Eq(x => x.Version, expected);
        var filter = f.And(f.Eq(x => x.Id, invoice.Id), f.Eq(x => x.TenantId, invoice.TenantId), versionFilter);

        invoice.LastUpdatedAt = DateTime.UtcNow;
        invoice.Version = expected + 1;
        var result = await _collection.ReplaceOneAsync(filter, invoice);
        if (result.MatchedCount == 0)
        {
            invoice.Version = expected;
            throw new ConcurrencyConflictException($"Invoice {invoice.InvoiceNumber} was changed by someone else; re-read it and retry");
        }
        _logger.LogInformation("Updated premium invoice {InvoiceNumber}", invoice.InvoiceNumber);
        return invoice;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IMongoClient, bool> ReplicaSets = new();

    /// <summary>Multi-document transactions need a replica set (or sharded cluster); a standalone mongod has none.</summary>
    private async Task<bool> SupportsTransactionsAsync()
    {
        var client = _collection.Database.Client;
        if (ReplicaSets.TryGetValue(client, out var known))
            return known;
        var hello = await client.GetDatabase("admin").RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("hello", 1));
        var supported = hello.Contains("setName") || (hello.TryGetValue("msg", out var msg) && msg == "isdbgrid");
        ReplicaSets[client] = supported;
        return supported;
    }

    public Task<PremiumInvoice> CreateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration) => WriteFencedAsync(invoice, create: true, leases, lease, now, duration);

    public Task<PremiumInvoice> UpdateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration) => WriteFencedAsync(invoice, create: false, leases, lease, now, duration);

    /// <summary>
    /// On a replica set: one transaction renews the lease only while this holder owns
    /// it and writes the invoice, so a holder whose lease was taken over writes
    /// nothing. On a standalone mongod (no transactions): renew (owner and unexpired),
    /// then write; the gap between the two is not fenced (documented).
    /// </summary>
    private async Task<PremiumInvoice> WriteFencedAsync(PremiumInvoice invoice, bool create, IBillingLeaseRepository leases,
        BillingLeaseHandle lease, DateTime now, TimeSpan duration)
    {
        if (leases is not BillingLeaseRepositoryMongo || !await SupportsTransactionsAsync())
        {
            if (!await leases.RenewAsync(lease, now, duration))
                throw new BillingLeaseLostException(lease.GroupNumber);
            return create ? await CreateAsync(invoice) : await UpdateAsync(invoice);
        }

        var tenantId = GetTenantId();
        var leaseCollection = _collection.Database.GetCollection<BillingLease>(BillingLeaseRepositoryMongo.CollectionName);
        var leaseId = BillingLease.IdFor(tenantId, lease.GroupNumber);
        var f = Builders<PremiumInvoice>.Filter;
        var expected = invoice.Version;
        if (create)
        {
            invoice.TenantId = tenantId;
            invoice.CreatedAt = DateTime.UtcNow;
        }
        invoice.LastUpdatedAt = DateTime.UtcNow;

        using var session = await _collection.Database.Client.StartSessionAsync();
        try
        {
            await session.WithTransactionAsync(async (s, ct) =>
            {
                var renewed = await leaseCollection.UpdateOneAsync(s,
                    Builders<BillingLease>.Filter.Where(l => l.Id == leaseId && l.TenantId == tenantId && l.LeaseOwner == lease.Owner),
                    Builders<BillingLease>.Update.Set(l => l.ExpiresAt, now + duration), cancellationToken: ct);
                if (renewed.MatchedCount == 0)
                    throw new BillingLeaseLostException(lease.GroupNumber);

                if (create)
                {
                    await _collection.InsertOneAsync(s, invoice, cancellationToken: ct);
                    return true;
                }
                var versionFilter = expected == 0
                    ? f.Or(f.Eq(x => x.Version, 0L), f.Exists(x => x.Version, false))
                    : f.Eq(x => x.Version, expected);
                invoice.Version = expected + 1;
                var replaced = await _collection.ReplaceOneAsync(s,
                    f.And(f.Eq(x => x.Id, invoice.Id), f.Eq(x => x.TenantId, invoice.TenantId), versionFilter), invoice, cancellationToken: ct);
                if (replaced.MatchedCount == 0)
                    throw new ConcurrencyConflictException($"Invoice {invoice.InvoiceNumber} was changed by someone else; re-read it and retry");
                return true;
            });
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InvoiceAlreadyExistsException(invoice.Id);
        }
        catch (Exception) when (!create)
        {
            invoice.Version = expected;
            throw;
        }
        _logger.LogInformation("Wrote premium invoice {InvoiceNumber} under the billing lease of group {GroupNumber}",
            invoice.InvoiceNumber, lease.GroupNumber);
        return invoice;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.Id, id),
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted premium invoice {Id}", id);
    }
}
