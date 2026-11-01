using MongoDB.Driver;
using CapitationService.Models;
using CloudHealthOffice.Infrastructure.Logging;

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
            new CreateIndexModel<CapitationContract>(keys.Ascending(c => c.TenantId).Ascending("PlanIds")),
            new CreateIndexModel<CapitationContract>(keys.Ascending(c => c.TenantId).Ascending(c => c.ContractId))
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

    public async Task<IEnumerable<CapitationContract>> GetByContractIdAsync(string contractId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationContract>.Filter.Eq(x => x.ContractId, contractId));
        return await _collection.Find(filter).SortByDescending(x => x.EffectiveDate).ToListAsync();
    }

    public async Task<ParentFieldsSyncResult> SyncParentFieldsAsync(string contractId, ParentContractFields fields, DateTime syncedAt)
    {
        var tenantId = GetTenantId();
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<CapitationContract>.Filter.Eq(x => x.ContractId, contractId));
        var update = Builders<CapitationContract>.Update
            .Set(x => x.ContractNumber, fields.ContractNumber)
            .Set(x => x.ProviderNPI, fields.ProviderNpi)
            .Set(x => x.ProviderName, fields.ProviderName)
            .Set(x => x.LineOfBusiness, fields.LineOfBusiness)
            .Set(x => x.LastDenormSyncAt, syncedAt)
            .Set(x => x.LastUpdatedAt, syncedAt)
            .Set(x => x.LastUpdatedBy, fields.SyncedBy)
            .Inc(x => x.Revision, 1L);
        // One statement per child, each atomic: a retry re-applies the same
        // values, so a sync that failed half way is finished by running it again.
        var result = await _collection.UpdateManyAsync(filter, update);
        _logger.LogInformation("Synced parent fields from contract {ContractNumber} to {Count} capitation rate configs",
            LogSanitizer.Sanitize(fields.ContractNumber), result.MatchedCount);
        return new ParentFieldsSyncResult(result.MatchedCount, syncedAt);
    }

    public async Task<CapitationContract> CreateAsync(CapitationContract contract)
    {
        contract.TenantId = GetTenantId();
        contract.CreatedAt = DateTime.UtcNow;
        contract.LastUpdatedAt = DateTime.UtcNow;
        contract.Revision = 0;
        await _collection.InsertOneAsync(contract);
        _logger.LogInformation("Created capitation contract {ContractNumber} for provider {NPI}",
            LogSanitizer.Sanitize(contract.ContractNumber), LogSanitizer.Sanitize(contract.ProviderNPI));
        return contract;
    }

    public async Task<CapitationContract> UpdateAsync(CapitationContract contract)
    {
        contract.LastUpdatedAt = DateTime.UtcNow;
        var filter = Builders<CapitationContract>.Filter.And(
            Builders<CapitationContract>.Filter.Eq(x => x.Id, contract.Id),
            Builders<CapitationContract>.Filter.Eq(x => x.TenantId, contract.TenantId));
        // Conditional on the revision the caller read: of two concurrent rate
        // edits (or an edit and an activate/terminate) only one is written.
        var expected = contract.Revision;
        contract.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), contract);
        }
        catch
        {
            contract.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            contract.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new CapitationConcurrencyException("Capitation contract", contract.Id, expected);
            // Missing: as before, nothing written and no error.
        }
        _logger.LogInformation("Updated capitation contract {ContractNumber}", LogSanitizer.Sanitize(contract.ContractNumber));
        return contract;
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<CapitationContract> RevisionIs(long expected)
    {
        var b = Builders<CapitationContract>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }
}
