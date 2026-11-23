using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services.CodeSets;
using MongoDB.Driver;

namespace CHO.TerminologyService.Data;

/// <summary>
/// Dated code sets in MongoDB: releases in <c>code_set_releases</c>, one row per
/// code per release in <c>code_validity_periods</c>. Global data (no tenant).
/// </summary>
public sealed class MongoCodeSetRepository : ICodeSetRepository
{
    private const int InsertBatchSize = 5_000;

    private readonly IMongoCollection<CodeSetRelease> _releases;
    private readonly IMongoCollection<CodeValidityPeriod> _periods;

    public MongoCodeSetRepository(IMongoDatabase database)
    {
        _releases = database.GetCollection<CodeSetRelease>("code_set_releases");
        _periods = database.GetCollection<CodeValidityPeriod>("code_validity_periods");

        _periods.Indexes.CreateMany(
        [
            new CreateIndexModel<CodeValidityPeriod>(
                Builders<CodeValidityPeriod>.IndexKeys.Ascending(x => x.System).Ascending(x => x.Code),
                new CreateIndexOptions { Name = "idx_code_validity_lookup" }),
            new CreateIndexModel<CodeValidityPeriod>(
                Builders<CodeValidityPeriod>.IndexKeys.Ascending(x => x.ReleaseId),
                new CreateIndexOptions { Name = "idx_code_validity_release" }),
        ]);
        _releases.Indexes.CreateOne(new CreateIndexModel<CodeSetRelease>(
            Builders<CodeSetRelease>.IndexKeys.Ascending(x => x.System).Ascending(x => x.EffectiveFrom),
            new CreateIndexOptions { Name = "idx_code_set_release_system" }));
    }

    public Task<List<CodeSetRelease>> GetReleasesAsync(string? system = null, CancellationToken ct = default)
    {
        var filter = system is null
            ? Builders<CodeSetRelease>.Filter.Empty
            : Builders<CodeSetRelease>.Filter.Eq(x => x.System, system);
        return _releases.Find(filter).SortBy(x => x.System).ThenBy(x => x.EffectiveFrom).ToListAsync(ct);
    }

    public Task<List<CodeValidityPeriod>> FindPeriodsAsync(string system, string code, CancellationToken ct = default)
        => _periods.Find(p => p.System == system && p.Code == code).ToListAsync(ct);

    public Task<List<CodeValidityPeriod>> FindPeriodsAsync(
        string system, IReadOnlyCollection<string> codes, CancellationToken ct = default)
        => _periods.Find(Builders<CodeValidityPeriod>.Filter.And(
                Builders<CodeValidityPeriod>.Filter.Eq(x => x.System, system),
                Builders<CodeValidityPeriod>.Filter.In(x => x.Code, codes)))
            .ToListAsync(ct);

    public async Task SaveReleaseAsync(
        CodeSetRelease release, IReadOnlyList<CodeValidityPeriod> periods, CancellationToken ct = default)
    {
        // Periods first, release last: a release is only visible (and so only
        // answers validity questions) once all of its codes are in.
        for (var i = 0; i < periods.Count; i += InsertBatchSize)
        {
            var batch = periods.Skip(i).Take(InsertBatchSize).ToList();
            await _periods.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
        }
        await _releases.ReplaceOneAsync(r => r.Id == release.Id, release, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task EndReleaseAsync(string releaseId, DateTime effectiveTo, CancellationToken ct = default)
    {
        await _releases.UpdateOneAsync(r => r.Id == releaseId,
            Builders<CodeSetRelease>.Update.Set(x => x.EffectiveTo, effectiveTo), cancellationToken: ct);

        var runsLater = Builders<CodeValidityPeriod>.Filter.And(
            Builders<CodeValidityPeriod>.Filter.Eq(x => x.ReleaseId, releaseId),
            Builders<CodeValidityPeriod>.Filter.Or(
                Builders<CodeValidityPeriod>.Filter.Eq(x => x.EffectiveTo, null),
                Builders<CodeValidityPeriod>.Filter.Gt(x => x.EffectiveTo, effectiveTo)));
        await _periods.UpdateManyAsync(runsLater,
            Builders<CodeValidityPeriod>.Update.Set(x => x.EffectiveTo, effectiveTo), cancellationToken: ct);

        // A period that now starts after it ends says nothing.
        await _periods.DeleteManyAsync(Builders<CodeValidityPeriod>.Filter.And(
            Builders<CodeValidityPeriod>.Filter.Eq(x => x.ReleaseId, releaseId),
            Builders<CodeValidityPeriod>.Filter.Gt(x => x.EffectiveFrom, effectiveTo)), ct);
    }

    public async Task DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        await _releases.DeleteOneAsync(r => r.Id == releaseId, ct);
        await _periods.DeleteManyAsync(p => p.ReleaseId == releaseId, ct);
    }
}
