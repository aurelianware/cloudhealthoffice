using ClaimsService.Models.Tpl;
using MongoDB.Driver;

namespace ClaimsService.Services.Tpl;

public interface ITplRecoveryRepository
{
    Task<TplRecoveryCase?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlySet<string>> ExistingKeysAsync(string tenantId, IReadOnlyCollection<string> caseKeys, CancellationToken ct = default);
    Task<IReadOnlyList<TplRecoveryCase>> ListAsync(string tenantId, TplRecoveryQuery query, CancellationToken ct = default);
    Task InsertManyAsync(IReadOnlyCollection<TplRecoveryCase> cases, CancellationToken ct = default);
    Task SaveAsync(TplRecoveryCase recoveryCase, CancellationToken ct = default);
}

public sealed record TplRecoveryQuery(
    string? State = null,
    TplRecoveryStatus? Status = null,
    bool OpenOnly = false,
    DateTime? DeadlineBefore = null,
    DateTime? PaidFrom = null,
    DateTime? PaidTo = null,
    string? MemberId = null,
    int Limit = 500);

public sealed class TplRecoveryRepositoryMongo : ITplRecoveryRepository
{
    public const string CollectionName = "TplRecoveryCases";
    private static readonly TplRecoveryStatus[] Closed =
        [TplRecoveryStatus.Recovered, TplRecoveryStatus.WrittenOff, TplRecoveryStatus.TransferredToState];

    private readonly IMongoCollection<TplRecoveryCase> _collection;

    public TplRecoveryRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<TplRecoveryCase>(CollectionName);
        var keys = Builders<TplRecoveryCase>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<TplRecoveryCase>(keys.Ascending(x => x.TenantId).Ascending(x => x.CaseKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<TplRecoveryCase>(keys.Ascending(x => x.TenantId).Ascending(x => x.State)
                .Ascending(x => x.Status).Ascending(x => x.RetentionDeadline)),
            new CreateIndexModel<TplRecoveryCase>(keys.Ascending(x => x.TenantId).Ascending(x => x.MemberId)),
        });
    }

    public async Task<TplRecoveryCase?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlySet<string>> ExistingKeysAsync(string tenantId, IReadOnlyCollection<string> caseKeys, CancellationToken ct = default)
    {
        if (caseKeys.Count == 0) return new HashSet<string>();
        var filter = Builders<TplRecoveryCase>.Filter.Eq(x => x.TenantId, tenantId)
                     & Builders<TplRecoveryCase>.Filter.In(x => x.CaseKey, caseKeys);
        var found = await _collection.Distinct(x => x.CaseKey, filter, cancellationToken: ct).ToListAsync(ct).ConfigureAwait(false);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<TplRecoveryCase>> ListAsync(string tenantId, TplRecoveryQuery query, CancellationToken ct = default)
    {
        var b = Builders<TplRecoveryCase>.Filter;
        var filter = b.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(query.State)) filter &= b.Eq(x => x.State, query.State.ToUpperInvariant());
        if (query.Status is { } status) filter &= b.Eq(x => x.Status, status);
        if (query.OpenOnly) filter &= b.Nin(x => x.Status, Closed);
        if (query.DeadlineBefore is { } before) filter &= b.Lt(x => x.RetentionDeadline, before);
        if (query.PaidFrom is { } from) filter &= b.Gte(x => x.PaymentDate, from);
        if (query.PaidTo is { } to) filter &= b.Lt(x => x.PaymentDate, to);
        if (!string.IsNullOrWhiteSpace(query.MemberId)) filter &= b.Eq(x => x.MemberId, query.MemberId);
        return await _collection.Find(filter).SortBy(x => x.PaymentDate).Limit(query.Limit).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertManyAsync(IReadOnlyCollection<TplRecoveryCase> cases, CancellationToken ct = default)
    {
        if (cases.Count == 0) return;
        try
        {
            await _collection.InsertManyAsync(cases, new InsertManyOptions { IsOrdered = false }, ct).ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<TplRecoveryCase> ex)
            when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // A concurrent sync opened the same case first; the unique key keeps one.
        }
    }

    public async Task SaveAsync(TplRecoveryCase recoveryCase, CancellationToken ct = default) =>
        await _collection.ReplaceOneAsync(
            x => x.TenantId == recoveryCase.TenantId && x.Id == recoveryCase.Id,
            recoveryCase, new ReplaceOptions { IsUpsert = false }, ct).ConfigureAwait(false);
}
