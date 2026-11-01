using CoverageService.Models.Tpl;
using MongoDB.Driver;

namespace CoverageService.Repositories;

/// <summary>Third-party liability resources per member (see <see cref="TplPolicy"/>).</summary>
public interface ITplPolicyRepository
{
    /// <summary>Every row for the member, any status, newest start first.</summary>
    Task<IReadOnlyList<TplPolicy>> GetByMemberAsync(string tenantId, string memberId, CancellationToken ct = default);

    /// <summary>Rows for these members from one state (any source), keyed for import matching.</summary>
    Task<IReadOnlyList<TplPolicy>> GetByMembersAsync(
        string tenantId, string state, IReadOnlyCollection<string> memberIds, CancellationToken ct = default);

    /// <summary>Active rows a source holds for a state, for full-file reconciliation.</summary>
    Task<IReadOnlyList<TplPolicy>> GetActiveBySourceAsync(
        string tenantId, string state, string source, CancellationToken ct = default);

    Task<TplPolicy?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Rows with a report still owed to the state (or, with
    /// <paramref name="includeReported"/>, also those already reported).</summary>
    Task<IReadOnlyList<TplPolicy>> GetStateReportRowsAsync(
        string tenantId, string state, bool includeReported, CancellationToken ct = default);

    /// <summary>Inserts or replaces by id.</summary>
    Task UpsertManyAsync(IReadOnlyCollection<TplPolicy> policies, CancellationToken ct = default);
}

public sealed class TplPolicyRepositoryMongo : ITplPolicyRepository
{
    public const string CollectionName = "TplPolicies";

    private readonly IMongoCollection<TplPolicy> _collection;

    public TplPolicyRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<TplPolicy>(CollectionName);
        var keys = Builders<TplPolicy>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<TplPolicy>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.NaturalKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<TplPolicy>(keys.Ascending(x => x.TenantId).Ascending(x => x.MemberId)),
            new CreateIndexModel<TplPolicy>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.State).Ascending(x => x.Source).Ascending(x => x.Status)),
        });
    }

    public async Task<IReadOnlyList<TplPolicy>> GetByMemberAsync(string tenantId, string memberId, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.MemberId == memberId)
            .SortByDescending(x => x.EffectiveDate)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<TplPolicy>> GetByMembersAsync(
        string tenantId, string state, IReadOnlyCollection<string> memberIds, CancellationToken ct = default)
    {
        if (memberIds.Count == 0) return [];
        var filter = Builders<TplPolicy>.Filter.Eq(x => x.TenantId, tenantId)
                     & Builders<TplPolicy>.Filter.Eq(x => x.State, state)
                     & Builders<TplPolicy>.Filter.In(x => x.MemberId, memberIds);
        return await _collection.Find(filter).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TplPolicy>> GetActiveBySourceAsync(
        string tenantId, string state, string source, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.State == state && x.Source == source
                                    && x.Status == TplPolicyStatus.Active)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<TplPolicy?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<TplPolicy>> GetStateReportRowsAsync(
        string tenantId, string state, bool includeReported, CancellationToken ct = default)
    {
        var b = Builders<TplPolicy>.Filter;
        var filter = b.Eq(x => x.TenantId, tenantId) & b.Eq(x => x.State, state);
        filter &= includeReported
            ? b.Or(b.Ne(x => x.PendingStateAction, null), b.Ne(x => x.ReportedToStateAt, null))
            : b.Ne(x => x.PendingStateAction, null);
        return await _collection.Find(filter).SortBy(x => x.StateReportDueBy).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<TplPolicy> policies, CancellationToken ct = default)
    {
        if (policies.Count == 0) return;
        var writes = policies.Select(p => (WriteModel<TplPolicy>)new ReplaceOneModel<TplPolicy>(
            Builders<TplPolicy>.Filter.Eq(x => x.Id, p.Id) & Builders<TplPolicy>.Filter.Eq(x => x.TenantId, p.TenantId), p)
        { IsUpsert = true });
        foreach (var batch in writes.Chunk(1000))
            await _collection.BulkWriteAsync(batch, new BulkWriteOptions { IsOrdered = false }, ct).ConfigureAwait(false);
    }
}
