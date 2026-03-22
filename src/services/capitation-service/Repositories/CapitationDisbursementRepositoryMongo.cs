using MongoDB.Driver;
using CapitationService.Models;

namespace CapitationService.Repositories;

/// <summary>
/// MongoDB implementation of capitation disbursement repository
/// </summary>
public class CapitationDisbursementRepositoryMongo : ICapitationDisbursementRepository
{
    private readonly IMongoCollection<CapitationDisbursement> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CapitationDisbursementRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor)
    {
        _collection = database.GetCollection<CapitationDisbursement>("capitation-disbursements");
        _httpContextAccessor = httpContextAccessor;

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        var keys = Builders<CapitationDisbursement>.IndexKeys;
        var models = new List<CreateIndexModel<CapitationDisbursement>>
        {
            new CreateIndexModel<CapitationDisbursement>(keys.Ascending(d => d.TenantId).Ascending(d => d.StatementId)),
            new CreateIndexModel<CapitationDisbursement>(keys.Ascending(d => d.TenantId).Ascending(d => d.Status)),
            new CreateIndexModel<CapitationDisbursement>(keys.Ascending(d => d.TenantId).Ascending(d => d.StripeTransferId))
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

    public async Task<CapitationDisbursement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.Id == id).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStatementIdAsync(string statementId)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.StatementId == statementId)
            .SortByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStatusAsync(DisbursementStatus status)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.Status == status)
            .SortByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetByStripeTransferIdAsync(string transferId)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.StripeTransferId == transferId)
            .ToListAsync();
    }

    public async Task<CapitationDisbursement> CreateAsync(CapitationDisbursement disbursement)
    {
        disbursement.TenantId = GetTenantId();
        await _collection.InsertOneAsync(disbursement);
        return disbursement;
    }

    public async Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement)
    {
        disbursement.LastUpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(
            d => d.TenantId == disbursement.TenantId && d.Id == disbursement.Id, disbursement);
        return disbursement;
    }
}
