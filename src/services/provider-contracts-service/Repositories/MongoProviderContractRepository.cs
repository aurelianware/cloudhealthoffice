using MongoDB.Driver;
using ProviderContractsService.Models;

namespace ProviderContractsService.Repositories;

public class MongoProviderContractRepository : IProviderContractRepository
{
    private static bool _indexesCreated;
    private static readonly object _indexLock = new();
    private readonly IMongoCollection<ProviderContract> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MongoProviderContractRepository> _logger;

    public MongoProviderContractRepository(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MongoProviderContractRepository> logger)
    {
        _collection = database.GetCollection<ProviderContract>("provider_contracts");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        if (_indexesCreated) return;
        lock (_indexLock)
        {
            if (_indexesCreated) return;
            CreateIndexes();
            _indexesCreated = true;
        }
    }

    private void CreateIndexes()
    {
        var keys = Builders<ProviderContract>.IndexKeys;
        var models = new List<CreateIndexModel<ProviderContract>>
        {
            new(keys.Ascending(c => c.TenantId).Ascending(c => c.ProviderNPI)),
            new(keys.Ascending(c => c.TenantId).Ascending(c => c.Status)),
            new(keys.Ascending(c => c.TenantId).Ascending(c => c.ContractNumber)),
            new(keys.Ascending(c => c.TenantId).Ascending(c => c.PaymentMethodology)),
            new(keys.Ascending(c => c.TenantId).Ascending(c => c.NetworkStatus))
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

    public async Task<ProviderContract?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.Id, id),
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<ProviderContract?> GetByContractNumberAsync(string number)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ProviderContract>.Filter.Eq(x => x.ContractNumber, number));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ProviderContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ProviderContractStatus? status = null,
        PaymentMethodology? paymentMethodology = null,
        NetworkParticipationStatus? networkStatus = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ProviderContract>>
        {
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(providerNpi))
            filters.Add(Builders<ProviderContract>.Filter.Eq(x => x.ProviderNPI, providerNpi));
        if (lob.HasValue)
            filters.Add(Builders<ProviderContract>.Filter.Eq(x => x.LineOfBusiness, lob.Value));
        if (status.HasValue)
            filters.Add(Builders<ProviderContract>.Filter.Eq(x => x.Status, status.Value));
        if (paymentMethodology.HasValue)
            filters.Add(Builders<ProviderContract>.Filter.Eq(x => x.PaymentMethodology, paymentMethodology.Value));
        if (networkStatus.HasValue)
            filters.Add(Builders<ProviderContract>.Filter.Eq(x => x.NetworkStatus, networkStatus.Value));

        return await _collection
            .Find(Builders<ProviderContract>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<ProviderContract> CreateAsync(ProviderContract contract)
    {
        contract.TenantId = GetTenantId();
        contract.CreatedAt = DateTime.UtcNow;
        contract.LastUpdatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(contract);
        _logger.LogInformation("Created provider contract {ContractNumber} for provider {NPI}",
            SanitizeForLog(contract.ContractNumber), SanitizeForLog(contract.ProviderNPI));
        return contract;
    }

    public async Task<ProviderContract> UpdateAsync(ProviderContract contract)
    {
        contract.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.Id, contract.Id),
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, contract.TenantId));
        await _collection.ReplaceOneAsync(filter, contract);
        _logger.LogInformation("Updated provider contract {ContractNumber}", SanitizeForLog(contract.ContractNumber));
        return contract;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
