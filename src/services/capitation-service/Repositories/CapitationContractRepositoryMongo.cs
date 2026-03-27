using MongoDB.Driver;
using CapitationService.Models;

namespace CapitationService.Repositories;

public class CapitationContractRepositoryMongo : ICapitationContractRepository
{
    private readonly IMongoCollection<CapitationContract> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationContractRepositoryMongo> _logger;

    public CapitationContractRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationContractRepositoryMongo> logger)
    {
        _collection = database.GetCollection<CapitationContract>("capitation-contracts");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var keys = Builders<CapitationContract>.IndexKeys;
        var models = new List<CreateIndexModel<CapitationContract>>
        {
            new CreateIndexModel<CapitationContract>(keys.Ascending(c => c.TenantId).Ascending(c => c.ProviderNPI)),
            new CreateIndexModel<CapitationContract>(keys.Ascending(c => c.TenantId).Ascending(c => c.Status)),
            new CreateIndexModel<CapitationContract>(keys.Ascending(c => c.TenantId).Ascending("PlanIds"))
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

    public async Task<CapitationContract?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.Id, id),
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<CapitationContract?> GetByProviderNpiAsync(string npi)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationContract>.Filter.Eq(x => x.ProviderNPI, npi),
            Builders<CapitationContract>.Filter.Eq(x => x.Status, CapitationRateConfigStatus.Active));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CapitationContract>> GetActiveContractsAsync(LineOfBusiness? lob = null, ContractType? type = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CapitationContract>>
        {
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationContract>.Filter.Eq(x => x.Status, CapitationRateConfigStatus.Active)
        };

        if (lob.HasValue)
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.LineOfBusiness, lob.Value));
        if (type.HasValue)
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.ContractType, type.Value));

        return await _collection
            .Find(Builders<CapitationContract>.Filter.And(filters))
            .SortBy(x => x.ProviderName)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationContract>> GetByPlanIdAsync(string planId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationContract>.Filter.AnyEq(x => x.PlanIds, planId),
            Builders<CapitationContract>.Filter.Eq(x => x.Status, CapitationRateConfigStatus.Active));
        return await _collection.Find(filter).SortBy(x => x.ProviderName).ToListAsync();
    }

    public async Task<IEnumerable<CapitationContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ContractType? type = null,
        CapitationRateConfigStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<CapitationContract>>
        {
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(providerNpi))
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.ProviderNPI, providerNpi));
        if (lob.HasValue)
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.LineOfBusiness, lob.Value));
        if (type.HasValue)
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.ContractType, type.Value));
        if (status.HasValue)
            filters.Add(Builders<CapitationContract>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<CapitationContract>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<CapitationContract> CreateAsync(CapitationContract contract)
    {
        contract.TenantId = GetTenantId();
        contract.CreatedAt = DateTime.UtcNow;
        contract.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(contract);
        _logger.LogInformation("Created capitation contract {ContractNumber} for provider {NPI}",
            SanitizeForLog(contract.ContractNumber), SanitizeForLog(contract.ProviderNPI));
        return contract;
    }

    public async Task<CapitationContract> UpdateAsync(CapitationContract contract)
    {
        contract.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.Id, contract.Id),
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, contract.TenantId));
        await _collection.ReplaceOneAsync(filter, contract);
        _logger.LogInformation("Updated capitation contract {ContractNumber}", SanitizeForLog(contract.ContractNumber));
        return contract;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
