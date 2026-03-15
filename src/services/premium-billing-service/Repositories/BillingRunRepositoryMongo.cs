using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public class BillingRunRepositoryMongo : IBillingRunRepository
{
    private readonly IMongoCollection<BillingRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BillingRunRepositoryMongo> _logger;

    public BillingRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BillingRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<BillingRun>("BillingRuns");
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

    public async Task<BillingRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<BillingRun>.Filter.And(
            Builders<BillingRun>.Filter.Eq(x => x.Id, id),
            Builders<BillingRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<BillingRun?> GetByBillingRunNumberAsync(string billingRunNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<BillingRun>.Filter.And(
            Builders<BillingRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<BillingRun>.Filter.Eq(x => x.BillingRunNumber, billingRunNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<BillingRun>> SearchAsync(DateTime? from, DateTime? to, BillingRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<BillingRun>>
        {
            Builders<BillingRun>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (from.HasValue)
            filters.Add(Builders<BillingRun>.Filter.Gte(x => x.BillingPeriod, from.Value));
        if (to.HasValue)
            filters.Add(Builders<BillingRun>.Filter.Lte(x => x.BillingPeriod, to.Value));
        if (status.HasValue)
            filters.Add(Builders<BillingRun>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<BillingRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<BillingRun> CreateAsync(BillingRun billingRun)
    {
        billingRun.TenantId = GetTenantId();
        billingRun.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(billingRun);
        _logger.LogInformation("Created billing run {BillingRunNumber}", billingRun.BillingRunNumber);
        return billingRun;
    }

    public async Task<BillingRun> UpdateAsync(BillingRun billingRun)
    {
        var filter = Builders<BillingRun>.Filter.And(
            Builders<BillingRun>.Filter.Eq(x => x.Id, billingRun.Id),
            Builders<BillingRun>.Filter.Eq(x => x.TenantId, billingRun.TenantId));
        await _collection.ReplaceOneAsync(filter, billingRun);
        _logger.LogInformation("Updated billing run {BillingRunNumber}", billingRun.BillingRunNumber);
        return billingRun;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<BillingRun>.Filter.And(
            Builders<BillingRun>.Filter.Eq(x => x.Id, id),
            Builders<BillingRun>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted billing run {Id}", id);
    }
}
