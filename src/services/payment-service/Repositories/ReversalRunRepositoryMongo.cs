using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

public class ReversalRunRepositoryMongo : IReversalRunRepository
{
    private readonly IMongoCollection<ReversalRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ReversalRunRepositoryMongo> _logger;

    public ReversalRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ReversalRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<ReversalRun>("ReversalRuns");
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

    public async Task<ReversalRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ReversalRun>.Filter.Eq(x => x.ReversalRunNumber, reversalRunNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ReversalRun>>
        {
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ReversalRun>.Filter.Gte(x => x.CreatedAt, from),
            Builders<ReversalRun>.Filter.Lte(x => x.CreatedAt, to)
        };

        if (status.HasValue)
            filters.Add(Builders<ReversalRun>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<ReversalRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<ReversalRun> CreateAsync(ReversalRun reversalRun)
    {
        reversalRun.TenantId = GetTenantId();
        reversalRun.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(reversalRun);
        _logger.LogInformation("Created reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);
        return reversalRun;
    }

    public async Task<ReversalRun> UpdateAsync(ReversalRun reversalRun)
    {
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, reversalRun.Id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, reversalRun.TenantId));
        await _collection.ReplaceOneAsync(filter, reversalRun);
        _logger.LogInformation("Updated reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);
        return reversalRun;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted reversal run {Id}", id);
    }
}
