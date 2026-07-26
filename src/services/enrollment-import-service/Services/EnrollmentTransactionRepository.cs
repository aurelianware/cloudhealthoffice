using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Services;

public interface IEnrollmentTransactionRepository
{
    Task<EnrollmentTransaction> CreateAsync(EnrollmentTransaction txn);
    Task<IReadOnlyList<EnrollmentTransaction>> ListByMemberAsync(
        string tenantId,
        string memberId,
        int limit = 100);

    /// <summary>Most recent transactions for a tenant, newest first — the admin-console read path.</summary>
    Task<IReadOnlyList<EnrollmentTransaction>> ListRecentAsync(string tenantId, int limit = 100);
}

/// <summary>
/// MongoDB repository for individual 834 transaction records. Indexed on
/// (tenantId, memberId, receivedAt) by <c>EnrollmentIndexInitializer</c>.
/// </summary>
public class EnrollmentTransactionRepository : IEnrollmentTransactionRepository
{
    private readonly IMongoCollection<EnrollmentTransaction> _collection;

    public EnrollmentTransactionRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<EnrollmentTransaction>("enrollment-transactions");
    }

    public async Task<EnrollmentTransaction> CreateAsync(EnrollmentTransaction txn)
    {
        if (string.IsNullOrEmpty(txn.Id)) txn.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(txn);
        return txn;
    }

    public async Task<IReadOnlyList<EnrollmentTransaction>> ListByMemberAsync(
        string tenantId, string memberId, int limit = 100)
    {
        var filter = Builders<EnrollmentTransaction>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<EnrollmentTransaction>.Filter.Eq(x => x.MemberId, memberId);

        return await _collection.Find(filter)
            .SortByDescending(x => x.ReceivedAt)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<EnrollmentTransaction>> ListRecentAsync(string tenantId, int limit = 100)
    {
        var filter = Builders<EnrollmentTransaction>.Filter.Eq(x => x.TenantId, tenantId);

        return await _collection.Find(filter)
            .SortByDescending(x => x.ReceivedAt)
            .Limit(limit)
            .ToListAsync();
    }
}
