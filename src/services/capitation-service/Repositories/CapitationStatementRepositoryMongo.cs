using MongoDB.Driver;
using CapitationService.Models;

namespace CapitationService.Repositories;

public class CapitationStatementRepositoryMongo : ICapitationStatementRepository
{
    private readonly IMongoCollection<CapitationStatement> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationStatementRepositoryMongo> _logger;

    public CapitationStatementRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationStatementRepositoryMongo> logger)
    {
        _collection = database.GetCollection<CapitationStatement>("capitation-statements");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var keys = Builders<CapitationStatement>.IndexKeys;
        var models = new List<CreateIndexModel<CapitationStatement>>
        {
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.ProviderNPI)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.CapitationRunId)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.Status)),
            new CreateIndexModel<CapitationStatement>(keys.Ascending(s => s.TenantId).Ascending(s => s.CapitationPeriodStart))
        };
        _collection.Indexes.CreateMany(models);
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<CapitationStatement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.Id, id),
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.CapitationRunId, runId));
        return await _collection.Find(filter).SortBy(x => x.ProviderName).ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CapitationStatement>>
        {
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.ProviderNPI, npi)
        };

        if (periodFrom.HasValue)
            filters.Add(Builders<CapitationStatement>.Filter.Gte(x => x.CapitationPeriodStart, periodFrom.Value));
        if (periodTo.HasValue)
            filters.Add(Builders<CapitationStatement>.Filter.Lte(x => x.CapitationPeriodStart, periodTo.Value));

        return await _collection
            .Find(Builders<CapitationStatement>.Filter.And(filters))
            .SortByDescending(x => x.CapitationPeriodStart)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Eq(x => x.Status, status));
        return await _collection.Find(filter).SortByDescending(x => x.CapitationPeriodStart).ToListAsync();
    }

    public async Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync()
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationStatement>.Filter.Ne(x => x.Status, CapitationStatementStatus.Paid),
            Builders<CapitationStatement>.Filter.Ne(x => x.Status, CapitationStatementStatus.Voided),
            Builders<CapitationStatement>.Filter.Gt(x => x.NetPayable, 0m));
        return await _collection.Find(filter).SortBy(x => x.CapitationPeriodStart).ToListAsync();
    }

    public async Task<CapitationStatement> CreateAsync(CapitationStatement statement)
    {
        statement.TenantId = GetTenantId();
        statement.CreatedAt = DateTime.UtcNow;
        statement.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(statement);
        _logger.LogInformation("Created capitation statement {StatementNumber} for provider {NPI}",
            statement.StatementNumber, statement.ProviderNPI);
        return statement;
    }

    public async Task<CapitationStatement> UpdateAsync(CapitationStatement statement)
    {
        statement.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<CapitationStatement>.Filter.And(
            Builders<CapitationStatement>.Filter.Eq(x => x.Id, statement.Id),
            Builders<CapitationStatement>.Filter.Eq(x => x.TenantId, statement.TenantId));
        await _collection.ReplaceOneAsync(filter, statement);
        _logger.LogInformation("Updated capitation statement {StatementNumber}", statement.StatementNumber);
        return statement;
    }
}
