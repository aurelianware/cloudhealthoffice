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
            Builders<PremiumInvoice>.Filter.Ne(x => x.Status, InvoiceStatus.Paid));
        return await _collection.Find(filter).SortBy(x => x.DueDate).ToListAsync();
    }

    public async Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, tenantId),
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
        await _collection.InsertOneAsync(invoice);
        _logger.LogInformation("Created premium invoice {InvoiceNumber} for group {GroupNumber}",
            invoice.InvoiceNumber, invoice.GroupNumber);
        return invoice;
    }

    public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
    {
        invoice.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<PremiumInvoice>.Filter.And(
            Builders<PremiumInvoice>.Filter.Eq(x => x.Id, invoice.Id),
            Builders<PremiumInvoice>.Filter.Eq(x => x.TenantId, invoice.TenantId));
        await _collection.ReplaceOneAsync(filter, invoice);
        _logger.LogInformation("Updated premium invoice {InvoiceNumber}", invoice.InvoiceNumber);
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
