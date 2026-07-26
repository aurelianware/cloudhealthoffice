using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Services;

public interface IEnrollmentImportRunRepository
{
    Task<EnrollmentImportRun> CreateAsync(EnrollmentImportRun run);

    /// <summary>Most recent import runs for a tenant, newest first — the admin-console read path.</summary>
    Task<IReadOnlyList<EnrollmentImportRun>> ListRecentAsync(string tenantId, int limit = 100);
}

/// <summary>
/// MongoDB repository for 834 import run summaries. Indexed on
/// (tenantId, startedAt) by <c>EnrollmentIndexInitializer</c>.
/// </summary>
public class EnrollmentImportRunRepository : IEnrollmentImportRunRepository
{
    private readonly IMongoCollection<EnrollmentImportRun> _collection;

    public EnrollmentImportRunRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<EnrollmentImportRun>("enrollment-import-runs");
    }

    public async Task<EnrollmentImportRun> CreateAsync(EnrollmentImportRun run)
    {
        if (string.IsNullOrEmpty(run.Id)) run.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(run);
        return run;
    }

    public async Task<IReadOnlyList<EnrollmentImportRun>> ListRecentAsync(string tenantId, int limit = 100)
    {
        var filter = Builders<EnrollmentImportRun>.Filter.Eq(x => x.TenantId, tenantId);

        return await _collection.Find(filter)
            .SortByDescending(x => x.StartedAt)
            .Limit(limit)
            .ToListAsync();
    }
}
