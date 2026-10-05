using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>
/// MongoDB implementation of EFT draft repository
/// </summary>
public class EftDraftRepositoryMongo : IEftDraftRepository
{
    /// <summary>Name of the unique partial index that allows one active draft per invoice.</summary>
    public const string ActiveDraftPerInvoiceIndex = "ux_tenant_activeInvoiceKey";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> InvoiceGuards = new();

    private readonly IMongoCollection<EftDraft> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<EftDraftRepositoryMongo>? _logger;

    public EftDraftRepositoryMongo(IMongoDatabase database, IHttpContextAccessor httpContextAccessor, ILogger<EftDraftRepositoryMongo>? logger = null)
    {
        _collection = database.GetCollection<EftDraft>("eftDrafts");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Once per collection: drafts written before the guard existed get their
    /// ActiveInvoiceKey, then the unique partial index (TenantId,
    /// ActiveInvoiceKey) where ActiveInvoiceKey is a string. If it cannot be
    /// built (two active drafts of one invoice already exist), no new draft is
    /// created until they are resolved; it is tried again on the next create.
    /// </summary>
    private Task EnsureInvoiceGuardAsync()
    {
        var key = $"{_collection.Database.DatabaseNamespace.DatabaseName}.{_collection.CollectionNamespace.CollectionName}";
        var guard = InvoiceGuards.GetOrAdd(key, _ => new Lazy<Task>(BuildInvoiceGuardAsync));
        var task = guard.Value;
        if (task.IsFaulted)
            InvoiceGuards.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, guard));
        return task;
    }

    private async Task BuildInvoiceGuardAsync()
    {
        var f = Builders<EftDraft>.Filter;
        await _collection.UpdateManyAsync(
            f.And(f.In(d => d.Status, EftDraft.ActiveStatuses), f.Not(f.Type(d => d.ActiveInvoiceKey, BsonType.String))),
            Builders<EftDraft>.Update.Pipeline(new[]
            {
                new BsonDocument("$set", new BsonDocument(nameof(EftDraft.ActiveInvoiceKey), "$" + nameof(EftDraft.InvoiceId)))
            }));
        try
        {
            await _collection.Indexes.CreateOneAsync(new CreateIndexModel<EftDraft>(
                Builders<EftDraft>.IndexKeys.Ascending(d => d.TenantId).Ascending(d => d.ActiveInvoiceKey),
                new CreateIndexOptions<EftDraft>
                {
                    Name = ActiveDraftPerInvoiceIndex,
                    Unique = true,
                    PartialFilterExpression = new BsonDocument(nameof(EftDraft.ActiveInvoiceKey), new BsonDocument("$type", "string")),
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex,
                "The one-active-draft-per-invoice index on eftDrafts could not be built (an invoice already has two active drafts?). " +
                "No new EFT draft is created until the duplicates are cancelled or settled");
            throw;
        }
    }

    private static bool IsDuplicateKey(MongoWriteException ex) => ex.WriteError?.Category == ServerErrorCategory.DuplicateKey;

    private string GetTenantId() =>
        _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenantId
            ? tenantId
            : throw new InvalidOperationException("TenantId not found in request context");

    public async Task<EftDraft> CreateAsync(EftDraft draft)
    {
        draft.TenantId = GetTenantId();
        draft.RefreshActiveInvoiceKey();
        await EnsureInvoiceGuardAsync();
        try
        {
            await _collection.InsertOneAsync(draft);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex) && draft.ActiveInvoiceKey != null)
        {
            throw new InvoiceDraftConflictException(draft.InvoiceId);
        }
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
        draft.RefreshActiveInvoiceKey();
        try
        {
            await _collection.ReplaceOneAsync(
                d => d.TenantId == draft.TenantId && d.Id == draft.Id, draft);
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex) && draft.ActiveInvoiceKey != null)
        {
            // Only an existing draft of an invoice that already had two active
            // drafts (from before the guard): its state is recorded, never lost.
            _logger?.LogCritical("Draft {DraftId}: invoice {InvoiceId} has another active draft; recorded without the invoice guard. Reconcile by hand",
                draft.Id, draft.InvoiceId);
            draft.ActiveInvoiceKey = null;
            await _collection.ReplaceOneAsync(
                d => d.TenantId == draft.TenantId && d.Id == draft.Id, draft);
        }
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

    public async Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy)
    {
        var tenantId = GetTenantId();
        var f = Builders<EftDraft>.Filter;
        var filter = f.And(
            f.Eq(d => d.TenantId, tenantId),
            f.Eq(d => d.Id, id),
            f.Eq(d => d.Status, EftDraftStatus.Pending));
        var update = Builders<EftDraft>.Update
            .Set(d => d.Status, EftDraftStatus.Releasing)
            .Set(d => d.ReleaseClaimId, claimId)
            .Set(d => d.ReleaseClaimedAt, claimedAt)
            .Set(d => d.ReleasedBy, releasedBy)
            .Set(d => d.LastUpdatedAt, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, update);
        return result.ModifiedCount == 1;
    }

    public async Task ReleaseClaimAsync(string id, string claimId, string? reason)
    {
        var tenantId = GetTenantId();
        var f = Builders<EftDraft>.Filter;
        var filter = f.And(
            f.Eq(d => d.TenantId, tenantId),
            f.Eq(d => d.Id, id),
            f.Eq(d => d.Status, EftDraftStatus.Releasing),
            f.Eq(d => d.ReleaseClaimId, claimId));
        await _collection.UpdateOneAsync(filter, Builders<EftDraft>.Update
            .Set(d => d.Status, EftDraftStatus.Pending)
            .Set(d => d.ReleaseClaimId, null)
            .Set(d => d.ReleaseClaimedAt, null)
            .Set(d => d.ReleasedBy, null)
            .Set(d => d.ErrorMessage, reason)
            .Set(d => d.LastUpdatedAt, DateTime.UtcNow));
    }

    public async Task<bool> TryCancelPendingAsync(string id, string cancelledBy)
    {
        var tenantId = GetTenantId();
        var f = Builders<EftDraft>.Filter;
        var filter = f.And(
            f.Eq(d => d.TenantId, tenantId),
            f.Eq(d => d.Id, id),
            f.Eq(d => d.Status, EftDraftStatus.Pending));
        var result = await _collection.UpdateOneAsync(filter, Builders<EftDraft>.Update
            .Set(d => d.Status, EftDraftStatus.Cancelled)
            .Set(d => d.ActiveInvoiceKey, null)
            .Set(d => d.LastUpdatedBy, cancelledBy)
            .Set(d => d.LastUpdatedAt, DateTime.UtcNow));
        return result.ModifiedCount == 1;
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
