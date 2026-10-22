using CloudHealthOffice.Infrastructure.Data;
using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Services;

public interface IEnrollmentImportRunRepository
{
    Task<EnrollmentImportRun> CreateAsync(EnrollmentImportRun run);

    /// <summary>Most recent import runs for a tenant, newest first — the admin-console read path.</summary>
    Task<IReadOnlyList<EnrollmentImportRun>> ListRecentAsync(string tenantId, int limit = 100);

    Task<EnrollmentImportRun?> GetAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>
    /// Takes the oldest queued run, or a running run whose lease has expired
    /// (its worker stopped), for <paramref name="owner"/> until
    /// <paramref name="leaseUntil"/>. Null when there is none.
    /// </summary>
    Task<EnrollmentImportRun?> TryClaimAsync(string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default);

    /// <summary>Extends the lease while <paramref name="owner"/> still holds it.</summary>
    Task<bool> RenewLeaseAsync(string tenantId, string id, string owner, DateTime leaseUntil, CancellationToken ct = default);

    Task SetExpectedUnitsAsync(string tenantId, string id, int expectedUnits, CancellationToken ct = default);

    /// <summary>Records the resume point and the unit counts as recounted from the unit records.</summary>
    Task SetProgressAsync(string tenantId, string id, int dispatchedThrough, int completedUnits, int failedUnits, CancellationToken ct = default);

    /// <summary>Adds one unit's outcome to the run's counts (atomic increments).</summary>
    Task AddUnitResultAsync(string tenantId, string id, ImportResult result, bool failed, CancellationToken ct = default);

    /// <summary>Running → Completed, exactly once: false when the run was already finished.</summary>
    Task<bool> TryCompleteAsync(string tenantId, string id, DateTime at, CancellationToken ct = default);

    /// <summary>Queued or Running → Failed, exactly once.</summary>
    Task<bool> TryFailAsync(string tenantId, string id, string kind, string message, DateTime at, CancellationToken ct = default);
}

/// <summary>
/// MongoDB repository for 834 import runs. Indexed on (tenantId, startedAt)
/// and (status, leaseExpiresAt) by <c>EnrollmentIndexInitializer</c>.
/// </summary>
public class EnrollmentImportRunRepository : IEnrollmentImportRunRepository
{
    /// <summary>The first errors are kept on the run; the rest stay on the transactions.</summary>
    public const int MaxErrors = 1000;

    private readonly IMongoCollection<EnrollmentImportRun> _collection;

    public EnrollmentImportRunRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<EnrollmentImportRun>("enrollment-import-runs");
    }

    private static FilterDefinition<EnrollmentImportRun> ById(string tenantId, string id) =>
        Builders<EnrollmentImportRun>.Filter.Where(x => x.TenantId == tenantId && x.Id == id);

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

    public async Task<EnrollmentImportRun?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _collection.Find(ById(tenantId, id)).FirstOrDefaultAsync(ct);

    [CrossTenantQuery("The import worker takes the next run of any tenant, then processes it under that run's own tenant.")]
    public async Task<EnrollmentImportRun?> TryClaimAsync(string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default)
    {
        var f = Builders<EnrollmentImportRun>.Filter;
        var filter = f.Or(
            f.Eq(x => x.Status, EnrollmentImportRunStatus.Queued),
            f.And(f.Eq(x => x.Status, EnrollmentImportRunStatus.Running), f.Lt(x => x.LeaseExpiresAt, now)));
        var update = Builders<EnrollmentImportRun>.Update
            .Set(x => x.Status, EnrollmentImportRunStatus.Running)
            .Set(x => x.LeaseOwner, owner)
            .Set(x => x.LeaseExpiresAt, leaseUntil)
            .Set(x => x.UpdatedAt, now)
            .Inc(x => x.Attempts, 1);
        return await _collection.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<EnrollmentImportRun>
        {
            Sort = Builders<EnrollmentImportRun>.Sort.Ascending(x => x.QueuedAt),
            ReturnDocument = ReturnDocument.After,
        }, ct);
    }

    public async Task<bool> RenewLeaseAsync(string tenantId, string id, string owner, DateTime leaseUntil, CancellationToken ct = default)
    {
        var filter = ById(tenantId, id) & Builders<EnrollmentImportRun>.Filter.Where(
            x => x.LeaseOwner == owner && x.Status == EnrollmentImportRunStatus.Running);
        var result = await _collection.UpdateOneAsync(filter,
            Builders<EnrollmentImportRun>.Update.Set(x => x.LeaseExpiresAt, leaseUntil), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task SetExpectedUnitsAsync(string tenantId, string id, int expectedUnits, CancellationToken ct = default) =>
        _collection.UpdateOneAsync(ById(tenantId, id),
            Builders<EnrollmentImportRun>.Update.Set(x => x.ExpectedUnits, expectedUnits).Set(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

    public Task SetProgressAsync(string tenantId, string id, int dispatchedThrough, int completedUnits, int failedUnits, CancellationToken ct = default) =>
        _collection.UpdateOneAsync(ById(tenantId, id),
            Builders<EnrollmentImportRun>.Update
                .Set(x => x.DispatchedThrough, dispatchedThrough)
                .Set(x => x.CompletedUnits, completedUnits)
                .Set(x => x.FailedUnits, failedUnits)
                .Set(x => x.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

    public Task AddUnitResultAsync(string tenantId, string id, ImportResult r, bool failed, CancellationToken ct = default)
    {
        var u = Builders<EnrollmentImportRun>.Update
            .Inc(x => failed ? x.FailedUnits : x.CompletedUnits, 1)
            .Inc(x => x.SuccessCount, r.SuccessCount)
            .Inc(x => x.FailedCount, r.FailedCount)
            .Inc(x => x.SkippedCount, r.SkippedCount)
            .Inc(x => x.MembersCreated, r.MembersCreated)
            .Inc(x => x.MembersUpdated, r.MembersUpdated)
            .Inc(x => x.MembersTerminated, r.MembersTerminated)
            .Inc(x => x.DependentsCreated, r.DependentsCreated)
            .Inc(x => x.DependentsUpdated, r.DependentsUpdated)
            .Inc(x => x.DependentsTerminated, r.DependentsTerminated)
            .Inc(x => x.DependentsFailed, r.DependentsFailed)
            .Inc(x => x.CoverageRecordsCreated, r.CoverageRecordsCreated)
            .Inc(x => x.CoverageRecordsUpdated, r.CoverageRecordsUpdated)
            .Inc(x => x.CoverageRecordsReinstated, r.CoverageRecordsReinstated)
            .Inc(x => x.CoverageRecordsTerminated, r.CoverageRecordsTerminated)
            .Inc(x => x.CoverageMappingsUnresolved, r.CoverageMappingsUnresolved)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);
        if (r.Errors.Count > 0)
            u = u.PushEach(x => x.Errors, r.Errors, slice: MaxErrors);
        return _collection.UpdateOneAsync(ById(tenantId, id), u, cancellationToken: ct);
    }

    public async Task<bool> TryCompleteAsync(string tenantId, string id, DateTime at, CancellationToken ct = default)
    {
        var filter = ById(tenantId, id) & Builders<EnrollmentImportRun>.Filter.Eq(x => x.Status, EnrollmentImportRunStatus.Running);
        var result = await _collection.UpdateOneAsync(filter, Builders<EnrollmentImportRun>.Update
            .Set(x => x.Status, EnrollmentImportRunStatus.Completed)
            .Set(x => x.CompletedAt, at)
            .Set(x => x.UpdatedAt, at)
            .Unset(x => x.LeaseOwner)
            .Unset(x => x.LeaseExpiresAt), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TryFailAsync(string tenantId, string id, string kind, string message, DateTime at, CancellationToken ct = default)
    {
        var filter = ById(tenantId, id) & Builders<EnrollmentImportRun>.Filter.In(x => x.Status,
            [EnrollmentImportRunStatus.Queued, EnrollmentImportRunStatus.Running]);
        var result = await _collection.UpdateOneAsync(filter, Builders<EnrollmentImportRun>.Update
            .Set(x => x.Status, EnrollmentImportRunStatus.Failed)
            .Set(x => x.FailureKind, kind)
            .Set(x => x.FailureMessage, message)
            .Set(x => x.CompletedAt, at)
            .Set(x => x.UpdatedAt, at)
            .Unset(x => x.LeaseOwner)
            .Unset(x => x.LeaseExpiresAt), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}

/// <summary>The unit records of import runs (collection <c>enrollment-import-units</c>).</summary>
public interface IEnrollmentImportUnitStore
{
    /// <summary>Records that the unit is being applied. False when it was already begun: it must not be applied again.</summary>
    Task<bool> TryBeginAsync(string tenantId, string runId, int sequence, string filePosition, DateTime at, CancellationToken ct = default);

    Task FinishAsync(string tenantId, string runId, int sequence, bool failed, string? error, DateTime at,
        string? exceptionId = null, CancellationToken ct = default);

    Task<int> CountAsync(string tenantId, string runId, string status, CancellationToken ct = default);

    /// <summary>
    /// Units left Applying by a worker that stopped are marked Failed (they
    /// may be partly applied, so they are not applied again). Returns their sequence numbers.
    /// </summary>
    Task<IReadOnlyList<int>> FailInterruptedAsync(string tenantId, string runId, DateTime at, CancellationToken ct = default);

    /// <summary>Failed units whose change has no enrollment exception yet (an interrupted unit, a recording failure).</summary>
    Task<IReadOnlyList<EnrollmentImportUnitRecord>> ListFailedWithoutExceptionAsync(string tenantId, string runId, CancellationToken ct = default);

    Task SetExceptionAsync(string tenantId, string runId, int sequence, string exceptionId, CancellationToken ct = default);

    /// <summary>The run's failed units, in file order.</summary>
    Task<IReadOnlyList<EnrollmentImportUnitRecord>> ListFailedAsync(string tenantId, string runId, int limit = 100, CancellationToken ct = default);
}

public sealed class EnrollmentImportUnitStore(IMongoDatabase database) : IEnrollmentImportUnitStore
{
    public const string CollectionName = "enrollment-import-units";
    /// <summary>Stored after the unit's file position; carries no member data.</summary>
    public const string InterruptedError = "The import stopped while this change was being applied, so it may be partly applied. It was not applied again; check the member's enrollment before reprocessing.";

    /// <summary>Stored after the unit's file position; the exception itself stays in the service log.</summary>
    public const string FailedError = "The change could not be applied: an unexpected error occurred; it has been logged.";

    private readonly IMongoCollection<EnrollmentImportUnitRecord> _collection =
        database.GetCollection<EnrollmentImportUnitRecord>(CollectionName);

    public async Task<bool> TryBeginAsync(string tenantId, string runId, int sequence, string filePosition, DateTime at, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(new EnrollmentImportUnitRecord
            {
                Id = EnrollmentImportUnitRecord.IdFor(runId, sequence),
                TenantId = tenantId,
                RunId = runId,
                Sequence = sequence,
                FilePosition = filePosition,
                StartedAt = at,
            }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task FinishAsync(string tenantId, string runId, int sequence, bool failed, string? error, DateTime at,
        string? exceptionId = null, CancellationToken ct = default)
    {
        var id = EnrollmentImportUnitRecord.IdFor(runId, sequence);
        return _collection.UpdateOneAsync(x => x.TenantId == tenantId && x.Id == id,
            Builders<EnrollmentImportUnitRecord>.Update
                .Set(x => x.Status, failed ? EnrollmentImportUnitStatus.Failed : EnrollmentImportUnitStatus.Done)
                .Set(x => x.Error, error)
                .Set(x => x.ExceptionId, exceptionId)
                .Set(x => x.FinishedAt, at),
            cancellationToken: ct);
    }

    public async Task<IReadOnlyList<EnrollmentImportUnitRecord>> ListFailedWithoutExceptionAsync(string tenantId, string runId, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.RunId == runId
                                    && x.Status == EnrollmentImportUnitStatus.Failed && x.ExceptionId == null)
            .SortBy(x => x.Sequence).ToListAsync(ct);

    public Task SetExceptionAsync(string tenantId, string runId, int sequence, string exceptionId, CancellationToken ct = default)
    {
        var id = EnrollmentImportUnitRecord.IdFor(runId, sequence);
        return _collection.UpdateOneAsync(x => x.TenantId == tenantId && x.Id == id,
            Builders<EnrollmentImportUnitRecord>.Update.Set(x => x.ExceptionId, exceptionId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<EnrollmentImportUnitRecord>> ListFailedAsync(string tenantId, string runId, int limit = 100, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.RunId == runId && x.Status == EnrollmentImportUnitStatus.Failed)
            .SortBy(x => x.Sequence).Limit(limit).ToListAsync(ct);

    public async Task<int> CountAsync(string tenantId, string runId, string status, CancellationToken ct = default) =>
        (int)await _collection.CountDocumentsAsync(x => x.TenantId == tenantId && x.RunId == runId && x.Status == status, cancellationToken: ct);

    public async Task<IReadOnlyList<int>> FailInterruptedAsync(string tenantId, string runId, DateTime at, CancellationToken ct = default)
    {
        var applying = await _collection.Find(x => x.TenantId == tenantId && x.RunId == runId && x.Status == EnrollmentImportUnitStatus.Applying)
            .Project(x => new { x.Sequence, x.FilePosition }).ToListAsync(ct);
        foreach (var unit in applying)
        {
            var id = EnrollmentImportUnitRecord.IdFor(runId, unit.Sequence);
            await _collection.UpdateOneAsync(
                x => x.TenantId == tenantId && x.Id == id && x.Status == EnrollmentImportUnitStatus.Applying,
                Builders<EnrollmentImportUnitRecord>.Update
                    .Set(x => x.Status, EnrollmentImportUnitStatus.Failed)
                    .Set(x => x.Error, $"{unit.FilePosition}: {InterruptedError}")
                    .Set(x => x.FinishedAt, at),
                cancellationToken: ct);
        }
        return applying.Select(u => u.Sequence).OrderBy(s => s).ToList();
    }
}
