using MongoDB.Driver;
using CapitationService.Models;

namespace CapitationService.Repositories;

public class CapitationRunRepositoryMongo : ICapitationRunRepository
{
    private readonly IMongoCollection<CapitationRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationRunRepositoryMongo> _logger;

    public CapitationRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<CapitationRun>("capitation-runs");
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

    public async Task<CapitationRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationRun>.Filter.And(
            Builders<CapitationRun>.Filter.Eq(x => x.Id, id),
            Builders<CapitationRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CapitationRun>> SearchAsync(DateTime? from = null, DateTime? to = null, CapitationRunStatus? status = null, LineOfBusiness? lineOfBusiness = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CapitationRun>>
        {
            Builders<CapitationRun>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (from.HasValue)
            filters.Add(Builders<CapitationRun>.Filter.Gte(x => x.CapitationPeriod, from.Value));
        if (to.HasValue)
            filters.Add(Builders<CapitationRun>.Filter.Lte(x => x.CapitationPeriod, to.Value));
        if (status.HasValue)
            filters.Add(Builders<CapitationRun>.Filter.Eq(x => x.Status, status.Value));
        if (lineOfBusiness.HasValue)
            filters.Add(Builders<CapitationRun>.Filter.Eq(x => x.LineOfBusiness, lineOfBusiness.Value));

        return await _collection
            .Find(Builders<CapitationRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationRun>> GetByStatusAsync(CapitationRunStatus status)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationRun>.Filter.And(
            Builders<CapitationRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationRun>.Filter.Eq(x => x.Status, status));
        return await _collection.Find(filter).SortByDescending(x => x.CreatedAt).ToListAsync();
    }

    public async Task<CapitationRun> CreateAsync(CapitationRun run)
    {
        run.TenantId = GetTenantId();
        run.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(run);
        _logger.LogInformation("Created capitation run {RunNumber}", run.RunNumber);
        return run;
    }

    public async Task<CapitationRun> UpdateAsync(CapitationRun run)
    {
        var filter = Builders<CapitationRun>.Filter.And(
            Builders<CapitationRun>.Filter.Eq(x => x.Id, run.Id),
            Builders<CapitationRun>.Filter.Eq(x => x.TenantId, run.TenantId));
        await _collection.ReplaceOneAsync(filter, run);
        _logger.LogInformation("Updated capitation run {RunNumber}", run.RunNumber);
        return run;
    }
}
