using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>
/// MongoDB implementation of EFT draft repository
/// </summary>
public class EftDraftRepositoryMongo : IEftDraftRepository
{
    private readonly IMongoCollection<EftDraft> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EftDraftRepositoryMongo(IMongoDatabase database, IHttpContextAccessor httpContextAccessor)
    {
        _collection = database.GetCollection<EftDraft>("eftDrafts");
        _httpContextAccessor = httpContextAccessor;
    }

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() ?? "default";

    public async Task<EftDraft> CreateAsync(EftDraft draft)
    {
        draft.TenantId = GetTenantId();
        await _collection.InsertOneAsync(draft);
        return draft;
    }

    public async Task<EftDraft?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.Id == id).FirstOrDefaultAsync();
    }

    public async Task<EftDraft> UpdateAsync(EftDraft draft)
    {
        draft.LastUpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(
            d => d.TenantId == draft.TenantId && d.Id == draft.Id, draft);
        return draft;
    }

    public async Task<IEnumerable<EftDraft>> GetByInvoiceIdAsync(string invoiceId)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.InvoiceId == invoiceId)
            .SortByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<EftDraft>> GetByStatusAsync(EftDraftStatus status)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.Status == status)
            .SortByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<EftDraft>> GetByStripePaymentIntentIdAsync(string paymentIntentId)
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d => d.TenantId == tenantId && d.StripePaymentIntentId == paymentIntentId)
            .ToListAsync();
    }

    public async Task<IEnumerable<EftDraft>> GetPendingDraftsAsync()
    {
        var tenantId = GetTenantId();
        return await _collection.Find(d =>
                d.TenantId == tenantId &&
                (d.Status == EftDraftStatus.Pending || d.Status == EftDraftStatus.Submitted))
            .SortBy(d => d.CreatedAt)
            .ToListAsync();
    }
}
