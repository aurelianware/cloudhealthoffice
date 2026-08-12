using CoverageService.Models;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// <summary>
/// Effective-dated history of PCP assignments. One row per change — prior rows
/// are closed (EndDate stamped), they are never mutated otherwise. Current
/// assignment = the row for a (tenantId, memberId) where EndDate is null.
/// </summary>
public interface IPcpAssignmentRepository
{
    /// <summary>Append a new assignment row.</summary>
    Task<PcpAssignment> AddAsync(PcpAssignment assignment);

    /// <summary>
    /// Close any currently-open assignment(s) for a member by stamping <paramref name="endDate"/>.
    /// Multiple open rows are possible in theory (one per coverage) but in practice
    /// we key on memberId for simplicity — callers that need per-coverage termination
    /// should filter in memory.
    /// </summary>
    Task<int> EndOpenAssignmentsAsync(string tenantId, string memberId, DateTime endDate);

    /// <summary>Current (open) assignment for a member, if any.</summary>
    Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId);

    /// <summary>Full history, newest first.</summary>
    Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId);

    /// <summary>Count of currently-open assignments to a given NPI. Used by panel reconciliation.</summary>
    Task<int> CountOpenByNpiAsync(string tenantId, string providerNpi);
}


/// <summary>
/// MongoDB implementation of <see cref="IPcpAssignmentRepository"/>.
/// </summary>
public sealed class PcpAssignmentRepositoryMongo : IPcpAssignmentRepository
{
    private readonly IMongoCollection<PcpAssignment> _collection;
    private const string CollectionName = "PcpAssignments";

    public PcpAssignmentRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<PcpAssignment>(CollectionName);

        var keys = Builders<PcpAssignment>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<PcpAssignment>(keys.Ascending(x => x.TenantId).Ascending(x => x.MemberId).Ascending(x => x.EndDate)),
            new CreateIndexModel<PcpAssignment>(keys.Ascending(x => x.TenantId).Ascending(x => x.ProviderNpi).Ascending(x => x.EndDate)),
            new CreateIndexModel<PcpAssignment>(keys.Ascending(x => x.TenantId).Ascending(x => x.MemberId).Descending(x => x.EffectiveDate))
        });
    }

    public async Task<PcpAssignment> AddAsync(PcpAssignment assignment)
    {
        assignment.CreatedDate = DateTime.UtcNow;
        if (string.IsNullOrEmpty(assignment.Id))
            assignment.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(assignment);
        return assignment;
    }

    public async Task<int> EndOpenAssignmentsAsync(string tenantId, string memberId, DateTime endDate)
    {
        var b = Builders<PcpAssignment>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.MemberId, memberId),
            b.Eq(x => x.EndDate, null));

        var update = Builders<PcpAssignment>.Update.Set(x => x.EndDate, endDate);
        var result = await _collection.UpdateManyAsync(filter, update);
        return (int)result.ModifiedCount;
    }

    public async Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId)
    {
        var b = Builders<PcpAssignment>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.MemberId, memberId),
            b.Eq(x => x.EndDate, null));

        return await _collection.Find(filter)
            .SortByDescending(x => x.EffectiveDate)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId)
    {
        var b = Builders<PcpAssignment>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.MemberId, memberId));

        return await _collection.Find(filter)
            .SortByDescending(x => x.EffectiveDate)
            .ToListAsync();
    }

    public async Task<int> CountOpenByNpiAsync(string tenantId, string providerNpi)
    {
        var b = Builders<PcpAssignment>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.ProviderNpi, providerNpi),
            b.Eq(x => x.EndDate, null));

        return (int)await _collection.CountDocumentsAsync(filter);
    }
}
