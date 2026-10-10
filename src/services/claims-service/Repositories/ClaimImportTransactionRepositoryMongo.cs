using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// MongoDB repository for individual 837 import transaction records.
/// Indexed on (tenantId, receivedAt) by <c>ClaimIndexInitializer</c>.
/// </summary>
public class ClaimImportTransactionRepositoryMongo : IClaimImportTransactionRepository
{
    public const string CollectionName = "claim-import-transactions";

    private readonly IMongoCollection<ClaimImportTransaction> _collection;

    public ClaimImportTransactionRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<ClaimImportTransaction>(CollectionName);
    }

    public async Task<ClaimImportTransaction> CreateAsync(ClaimImportTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Id)) txn.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(txn);
        return txn;
    }

    public async Task<IReadOnlyList<ClaimImportTransaction>> ListRecentAsync(string tenantId, int limit = 100)
    {
        var filter = Builders<ClaimImportTransaction>.Filter.Eq(x => x.TenantId, tenantId);

        return await _collection.Find(filter)
            .SortByDescending(x => x.ReceivedAt)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ClaimImportTransaction>> ListWithSnipWarningsAsync(
        string tenantId, string? ruleId = null, int? level = null, string? submitterId = null, int limit = 100)
    {
        var f = Builders<ClaimImportTransaction>.Filter;
        var warning = Builders<SnipFindingRecord>.Filter.Empty;
        if (ruleId is not null) warning &= Builders<SnipFindingRecord>.Filter.Eq(w => w.RuleId, ruleId);
        if (level is not null) warning &= Builders<SnipFindingRecord>.Filter.Eq(w => w.Level, level.Value);

        var filter = f.Eq(x => x.TenantId, tenantId) & f.ElemMatch(x => x.SnipWarnings, warning);
        if (submitterId is not null)
            filter &= f.Eq(x => x.SubmitterIdNormalized, submitterId) | f.Eq(x => x.ApplicationSenderCodeNormalized, submitterId);

        return await _collection.Find(filter)
            .SortByDescending(x => x.ReceivedAt)
            .Limit(limit)
            .ToListAsync();
    }
}
