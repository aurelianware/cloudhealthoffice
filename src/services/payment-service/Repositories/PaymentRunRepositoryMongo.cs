using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

public class PaymentRunRepositoryMongo : IPaymentRunRepository
{
    private readonly IMongoCollection<PaymentRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRunRepositoryMongo> _logger;

    public PaymentRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<PaymentRun>("PaymentRuns");
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

    public async Task<PaymentRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PaymentRun>.Filter.Eq(x => x.PaymentRunNumber, paymentRunNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<PaymentRun>>
        {
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<PaymentRun>.Filter.Gte(x => x.CreatedAt, from),
            Builders<PaymentRun>.Filter.Lte(x => x.CreatedAt, to)
        };

        if (status.HasValue)
            filters.Add(Builders<PaymentRun>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<PaymentRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<PaymentRun> CreateAsync(PaymentRun paymentRun)
    {
        paymentRun.TenantId = GetTenantId();
        paymentRun.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(paymentRun);
        _logger.LogInformation("Created payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);
        return paymentRun;
    }

    public async Task<PaymentRun> UpdateAsync(PaymentRun paymentRun)
    {
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, paymentRun.Id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, paymentRun.TenantId));
        await _collection.ReplaceOneAsync(filter, paymentRun);
        _logger.LogInformation("Updated payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);
        return paymentRun;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<PaymentRun>.Filter.And(
            Builders<PaymentRun>.Filter.Eq(x => x.Id, id),
            Builders<PaymentRun>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted payment run {Id}", id);
    }
}
