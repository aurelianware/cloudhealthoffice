using CloudHealthOffice.Infrastructure.Data;
using CoverageService.Exceptions;
using CoverageService.Models;
using CoverageService.Services;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// <summary>
/// MongoDB repository for Coverage entities (the only coverage store).
/// Every query is scoped by TenantId for multi-tenant isolation.
/// </summary>
public class CoverageRepositoryMongo : ICoverageRepository
{
    private readonly IMongoCollection<Coverage> _collection;
    public const string CollectionName = "Coverage";

    private readonly PcpMongoSession? _pcpSession;
    private readonly PcpRosterWriteGuard? _rosterGuard;

    public CoverageRepositoryMongo(IMongoDatabase database, PcpMongoSession? pcpSession = null, PcpRosterWriteGuard? rosterGuard = null)
    {
        _pcpSession = pcpSession;
        _rosterGuard = rosterGuard;
        _collection = database.GetCollection<Coverage>(CollectionName);
        
        // Ensure indexes exist (best effort on startup)
        var indexKeys = Builders<Coverage>.IndexKeys;
        var indexModels = new List<CreateIndexModel<Coverage>>
        {
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.MemberId)),
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.GroupNumber)),
            // Compound index for active coverage search
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.MemberId).Ascending(c => c.Status).Ascending(c => c.EffectiveDate)),
            // PCP panel roster lookup (capitation-service queries by provider NPI)
            new CreateIndexModel<Coverage>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.PcpNpi).Ascending(c => c.Status))
        };
        
        _collection.Indexes.CreateMany(indexModels);
    }

    public async Task<Coverage?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, id)
        );

        return await (_pcpSession?.Current is { } session ? _collection.Find(session, filter) : _collection.Find(filter)).FirstOrDefaultAsync();
    }

    public async Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null)
    {
        // Filter: tenant and member; status in Coverage.DateOfServiceStatuses;
        // effectiveDate <= serviceDate; and either no termination date (only
        // when not Terminated) or terminationDate >= serviceDate.

        var builder = Builders<Coverage>.Filter;
        var date = serviceDate.Date;

        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.MemberId, memberId),
            // In force on DOS is decided by the date span, not current status
            // (see Coverage.DateOfServiceStatuses).
            builder.In(c => c.Status, Coverage.DateOfServiceStatuses),
            builder.Lte(c => c.EffectiveDate, date),
            builder.Or(
                // Open-ended only for non-Terminated coverage: a Terminated
                // record with no termination date fails closed.
                builder.And(
                    builder.Eq(c => c.TerminationDate, null),
                    builder.Ne(c => c.Status, CoverageStatus.Terminated)),
                builder.Gte(c => c.TerminationDate, date)
            )
        );

        if (!string.IsNullOrEmpty(insuranceLineCode))
        {
            filter = builder.And(filter, builder.Eq(c => c.InsuranceLineCode, insuranceLineCode));
        }

        return await (_pcpSession?.Current is { } session
            ? _collection.Find(session, filter) : _collection.Find(filter)).ToListAsync();
    }

    public async Task<IReadOnlySet<string>> GetKnownMemberIdsAsync(
        string tenantId, IReadOnlyCollection<string> memberIds, CancellationToken ct = default)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in memberIds.Distinct().Chunk(5000))
        {
            var filter = Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId)
                         & Builders<Coverage>.Filter.In(c => c.MemberId, batch);
            var found = await _collection.Distinct(c => c.MemberId, filter, cancellationToken: ct).ToListAsync(ct);
            known.UnionWith(found);
        }
        return known;
    }

    public async Task<List<Coverage>> GetCoverageHistoryAsync(
        string tenantId,
        string memberId,
        bool includeTerminated = true)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.MemberId, memberId)
        );

        if (!includeTerminated)
        {
            // Terminated = status Terminated, or a termination date already
            // reached that the daily status sweep has not flipped yet.
            filter = builder.And(filter,
                builder.Ne(c => c.Status, CoverageStatus.Terminated),
                NotTerminatedAsOf(DateTime.UtcNow.Date));
        }

        var sort = Builders<Coverage>.Sort.Descending(c => c.EffectiveDate);

        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    public async Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.Eq(c => c.TenantId, tenantId);

        if (!string.IsNullOrEmpty(memberId))
        {
            filter = builder.And(filter, builder.Eq(c => c.MemberId, memberId));
        }

        if (!string.IsNullOrEmpty(groupNumber))
        {
            filter = builder.And(filter, builder.Eq(c => c.GroupNumber, groupNumber));
        }

        if (!string.IsNullOrEmpty(planId))
        {
            filter = builder.And(filter, builder.Eq(c => c.PlanId, planId));
        }

        if (activeOnly)
        {
            // Currently active (Coverage.CurrentStatus): Active, or Pending
            // whose effective date has arrived, with the termination date (if
            // any) not yet reached — so the listing doesn't depend on when the
            // daily status sweep last ran.
            filter = builder.And(filter, CurrentlyActiveAsOf(DateTime.UtcNow.Date));
        }

        // Pagination: the continuation token is an integer skip, or 0 when absent.
        // WARNING: This is a simplified implementation. Real production apps might need cursor-based pagination.
        
        int skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out int tokenSkip))
        {
            skip = tokenSkip;
        }

        var results = await _collection.Find(filter)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        string? newToken = null;
        if (results.Count == pageSize)
        {
            newToken = (skip + pageSize).ToString();
        }

        return (results, newToken);
    }

    public async Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.GroupNumber, groupNumber)
        );

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<List<Coverage>> GetByPcpNpiAsync(
        string tenantId,
        string pcpNpi,
        CoverageStatus? status = null,
        LineOfBusiness? lineOfBusiness = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.PcpNpi, pcpNpi)
        );

        if (status == CoverageStatus.Active)
        {
            filter = builder.And(filter, CurrentlyActiveAsOf(DateTime.UtcNow.Date));
        }
        else if (status.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.LineOfBusiness, lineOfBusiness.Value));
        }

        return await (_pcpSession?.Current is { } session
            ? _collection.Find(session, filter) : _collection.Find(filter)).ToListAsync();
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null)
    {
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.GroupNumber, groupNumber)
        );

        if (status.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, status.Value));
        }

        var count = await _collection.CountDocumentsAsync(filter);
        return (int)count;
    }

    [CrossTenantQuery("CoverageStatusSweepJob (background): finds due transitions across tenants; SetStatusAsync filters on each coverage's TenantId.")]
    public async Task<List<Coverage>> GetStatusTransitionsDueAsync(DateTime today, int maxItems)
    {
        var builder = Builders<Coverage>.Filter;
        // In-force coverage whose termination date is reached, or Pending
        // whose effective date arrived (Coverage.DueStatusTransition). Only
        // the statuses the sweep will actually change, so rows it skips
        // (Suspended, unknown) never fill a batch and stall it.
        var filter = builder.Or(
            builder.And(
                builder.In(c => c.Status, Coverage.AutoTerminatedStatuses),
                builder.Ne(c => c.TerminationDate, null),
                builder.Lte(c => c.TerminationDate, today.Date)),
            builder.And(
                builder.Eq(c => c.Status, CoverageStatus.Pending),
                builder.Lte(c => c.EffectiveDate, today.Date)));

        return await _collection.Find(filter).Limit(maxItems).ToListAsync();
    }

    public async Task<bool> SetStatusAsync(Coverage observed, CoverageStatus newStatus, string updatedBy)
    {
        if (_rosterGuard is null) return await SetStatusCoreAsync(observed, newStatus, updatedBy);
        try { return await _rosterGuard.ExecuteAsync(observed, () => SetStatusCoreAsync(observed, newStatus, updatedBy), newStatus: newStatus); }
        catch (CoverageConcurrencyException) { return false; }
    }

    private async Task<bool> SetStatusCoreAsync(Coverage observed, CoverageStatus newStatus, string updatedBy)
    {
        // Only the status/audit fields, and only while the status and the
        // dates the transition was decided from are still the ones the sweep
        // read, so a concurrent edit (PCP change, reinstatement - which clears
        // the termination date and may leave the status unchanged) is neither
        // overwritten nor undone.
        var builder = Builders<Coverage>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, observed.TenantId),
            builder.Eq(c => c.Id, observed.Id),
            builder.Eq(c => c.Status, observed.Status),
            builder.Eq(c => c.EffectiveDate, observed.EffectiveDate),
            builder.Eq(c => c.TerminationDate, observed.TerminationDate),
            RevisionIs(observed.Revision));
        var update = Builders<Coverage>.Update
            .Set(c => c.Status, newStatus)
            .Set(c => c.LastUpdatedDate, DateTime.UtcNow)
            .Set(c => c.LastUpdatedBy, updatedBy)
            // Bumped: a replace based on a read from before the sweep then
            // conflicts instead of turning the coverage back.
            .Inc(c => c.Revision, 1L);

        var result = _pcpSession?.Current is { } session
            ? await _collection.UpdateOneAsync(session, filter, update)
            : await _collection.UpdateOneAsync(filter, update);
        return result.ModifiedCount > 0;
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Coverage> RevisionIs(long expected)
    {
        var b = Builders<Coverage>.Filter;
        return expected == 0
            ? b.Or(b.Eq(c => c.Revision, 0L), b.Exists(c => c.Revision, false))
            : b.Eq(c => c.Revision, expected);
    }

    public Task<Coverage> CreateAsync(Coverage coverage) => _rosterGuard is null
        ? CreateCoreAsync(coverage) : _rosterGuard.ExecuteAsync(coverage, () => CreateCoreAsync(coverage), creating: true);

    private async Task<Coverage> CreateCoreAsync(Coverage coverage)
    {
        coverage.CreatedDate = DateTime.UtcNow;
        coverage.LastUpdatedDate = DateTime.UtcNow;
        // Ensure ID is set if not already
        if (string.IsNullOrEmpty(coverage.Id))
        {
            coverage.Id = Guid.NewGuid().ToString();
        }
        coverage.Revision = 0;

        if (_pcpSession?.Current is { } session) await _collection.InsertOneAsync(session, coverage);
        else await _collection.InsertOneAsync(coverage);
        return coverage;
    }

    public Task<Coverage> UpdateAsync(Coverage coverage) => _rosterGuard is null
        ? UpdateCoreAsync(coverage) : _rosterGuard.ExecuteAsync(coverage, () => UpdateCoreAsync(coverage));

    private async Task<Coverage> UpdateCoreAsync(Coverage coverage)
    {
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, coverage.TenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, coverage.Id)
        );

        // Conditional on the revision the caller read: a concurrent write in
        // between (the status sweep, a PCP assignment, another edit) makes this
        // one fail instead of being silently overwritten or undone.
        var expected = coverage.Revision;
        coverage.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = _pcpSession?.Current is { } session
                ? await _collection.ReplaceOneAsync(session, filter & RevisionIs(expected), coverage)
                : await _collection.ReplaceOneAsync(filter & RevisionIs(expected), coverage);
        }
        catch
        {
            coverage.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            coverage.Revision = expected;
            if (await (_pcpSession?.Current is { } active ? _collection.Find(active, filter) : _collection.Find(filter)).AnyAsync())
                throw new CoverageConcurrencyException(coverage.Id, expected);

            throw new Exception($"Coverage with ID {coverage.Id} not found for update.");
        }

        return coverage;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        if (_rosterGuard is null) { await DeleteCoreAsync(tenantId, id); return; }
        var observed = await GetByIdAsync(tenantId, id);
        if (observed is null) return;
        await _rosterGuard.ExecuteAsync(observed, async () => { await DeleteCoreAsync(tenantId, id); return true; }, deleting: true);
    }

    private async Task DeleteCoreAsync(string tenantId, string id)
    {
        var filter = Builders<Coverage>.Filter.And(
            Builders<Coverage>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Coverage>.Filter.Eq(c => c.Id, id)
        );

        if (_pcpSession?.Current is { } session) await _collection.DeleteOneAsync(session, filter);
        else await _collection.DeleteOneAsync(filter);
    }

    // Termination date not yet reached (or none).
    private static FilterDefinition<Coverage> NotTerminatedAsOf(DateTime today) =>
        Builders<Coverage>.Filter.Or(
            Builders<Coverage>.Filter.Eq(c => c.TerminationDate, null),
            Builders<Coverage>.Filter.Gt(c => c.TerminationDate, today));

    // Coverage.CurrentStatus == Active. An effective COBRA Pending coverage is
    // currently COBRA, not Active (Ne true also matches records without the flag).
    private static FilterDefinition<Coverage> CurrentlyActiveAsOf(DateTime today)
    {
        var b = Builders<Coverage>.Filter;
        return b.And(
            b.Or(
                b.Eq(c => c.Status, CoverageStatus.Active),
                b.And(
                    b.Eq(c => c.Status, CoverageStatus.Pending),
                    b.Lte(c => c.EffectiveDate, today),
                    b.Ne(c => c.IsCOBRA, true))),
            NotTerminatedAsOf(today));
    }
}
