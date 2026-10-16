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
        disbursement.Revision = 0;
        await _collection.InsertOneAsync(disbursement);
        return disbursement;
    }

    public async Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationDisbursement>.Filter;
        var filter = f.And(
            f.Eq(d => d.TenantId, tenantId),
            f.Eq(d => d.Id, id),
            f.Eq(d => d.Status, DisbursementStatus.Pending));
        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<CapitationDisbursement>.Update
            .Set(d => d.Status, DisbursementStatus.Releasing)
            .Set(d => d.ReleaseClaimId, claimId)
            .Set(d => d.ReleaseClaimedAt, claimedAt)
            .Set(d => d.ReleasedBy, releasedBy)
            .Set(d => d.LastUpdatedAt, DateTime.UtcNow)));
        return result.ModifiedCount == 1;
    }

    public async Task ReleaseClaimAsync(string id, string claimId, string? reason)
    {
        var tenantId = GetTenantId();
        var f = Builders<CapitationDisbursement>.Filter;
        var filter = f.And(
            f.Eq(d => d.TenantId, tenantId),
            f.Eq(d => d.Id, id),
            f.Eq(d => d.Status, DisbursementStatus.Releasing),
            f.Eq(d => d.ReleaseClaimId, claimId));
        await _collection.UpdateOneAsync(filter, Bumped(Builders<CapitationDisbursement>.Update
            .Set(d => d.Status, DisbursementStatus.Pending)
            .Set(d => d.ReleaseClaimId, null)
            .Set(d => d.ReleaseClaimedAt, null)
            .Set(d => d.ReleasedBy, null)
            .Set(d => d.ErrorMessage, reason)
            .Set(d => d.LastUpdatedAt, DateTime.UtcNow)));
    }

    /// <summary>Every targeted write to a disbursement bumps its <see cref="CapitationDisbursement.Revision"/>.</summary>
    private static UpdateDefinition<CapitationDisbursement> Bumped(UpdateDefinition<CapitationDisbursement> update) =>
        update.Inc(d => d.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<CapitationDisbursement> RevisionIs(long expected)
    {
        var b = Builders<CapitationDisbursement>.Filter;
        return expected == 0
            ? b.Or(b.Eq(d => d.Revision, 0L), b.Exists(d => d.Revision, false))
            : b.Eq(d => d.Revision, expected);
    }

    public async Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement)
    {
        disbursement.LastUpdatedAt = DateTime.UtcNow;
        var f = Builders<CapitationDisbursement>.Filter;
        var filter = f.And(f.Eq(d => d.TenantId, disbursement.TenantId), f.Eq(d => d.Id, disbursement.Id));
        // Conditional on the revision the caller read: a replace from a stale
        // read (a cancel read while Pending) can never undo a conditional write
        // made since (TryClaimForReleaseAsync's Pending to Releasing).
        var expected = disbursement.Revision;
        disbursement.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), disbursement);
        }
        catch
        {
            disbursement.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            disbursement.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new CapitationConcurrencyException("Capitation disbursement", disbursement.Id, expected);
            // Missing: as before, nothing written and no error.
        }
        return disbursement;
    }
}
