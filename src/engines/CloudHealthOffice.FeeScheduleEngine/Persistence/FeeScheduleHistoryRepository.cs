using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// Change history of the fee schedule store: a <see cref="FeeScheduleChange"/>
/// for every create and replace, and a <see cref="FeeScheduleRevisionSnapshot"/>
/// of every revision a replace overwrote. Retro rate reprocessing reads it to
/// find the paid claims a rate change touches and the rates they were paid on.
/// </summary>
public interface IFeeScheduleHistoryRepository
{
    /// <summary>
    /// Records a write the store just made: <paramref name="before"/> is the
    /// schedule as it stood (null for a create), <paramref name="after"/> as
    /// now stored, with its new revision. Idempotent per stored revision.
    /// </summary>
    Task<FeeScheduleChange> RecordAsync(FeeSchedule? before, FeeSchedule after, CancellationToken ct = default);

    /// <summary>
    /// The tenant's changes, newest first, optionally for one schedule and
    /// since a time. Changed-code lists are included.
    /// </summary>
    Task<IReadOnlyList<FeeScheduleChange>> ListChangesAsync(
        string tenantId, string? feeScheduleId = null, DateTime? since = null, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// The schedule as it stood at <paramref name="revision"/>: a snapshot
    /// when a replace has since overwritten it, otherwise null (read the live
    /// schedule, which is that revision or newer).
    /// </summary>
    Task<FeeScheduleRevisionSnapshot?> GetRevisionAsync(
        string tenantId, string feeScheduleId, long revision, CancellationToken ct = default);
}

/// <summary>MongoDB store for <see cref="IFeeScheduleHistoryRepository"/>.</summary>
public sealed class FeeScheduleHistoryRepositoryMongo : IFeeScheduleHistoryRepository
{
    private readonly IMongoCollection<FeeScheduleChange> _changes;
    private readonly IMongoCollection<FeeScheduleRevisionSnapshot> _revisions;
    private readonly TimeProvider _clock;

    public FeeScheduleHistoryRepositoryMongo(IMongoDatabase database, IConfiguration configuration)
        : this(
            database,
            configuration["FeeScheduleEngine:FeeScheduleChangeCollection"] ?? "FeeScheduleChanges",
            configuration["FeeScheduleEngine:FeeScheduleRevisionCollection"] ?? "FeeScheduleRevisions",
            TimeProvider.System)
    {
    }

    public FeeScheduleHistoryRepositoryMongo(
        IMongoDatabase database, string changeCollection, string revisionCollection, TimeProvider clock)
    {
        _changes = database.GetCollection<FeeScheduleChange>(changeCollection);
        _revisions = database.GetCollection<FeeScheduleRevisionSnapshot>(revisionCollection);
        _clock = clock;
    }

    public async Task<FeeScheduleChange> RecordAsync(FeeSchedule? before, FeeSchedule after, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (before is not null)
        {
            var snapshot = new FeeScheduleRevisionSnapshot
            {
                Id = FeeScheduleRevisionSnapshot.MakeId(before.Id, before.Revision),
                TenantId = before.TenantId,
                FeeScheduleId = before.Id,
                Revision = before.Revision,
                Schedule = before,
                SupersededAt = now,
            };
            await _revisions.ReplaceOneAsync(
                Builders<FeeScheduleRevisionSnapshot>.Filter.Eq(x => x.Id, snapshot.Id),
                snapshot, new ReplaceOptions { IsUpsert = true }, ct);
        }

        var change = FeeScheduleChangeDetector.Compare(before, after, now);
        await _changes.ReplaceOneAsync(
            Builders<FeeScheduleChange>.Filter.Eq(x => x.Id, change.Id),
            change, new ReplaceOptions { IsUpsert = true }, ct);
        return change;
    }

    public async Task<IReadOnlyList<FeeScheduleChange>> ListChangesAsync(
        string tenantId, string? feeScheduleId = null, DateTime? since = null, int limit = 50, CancellationToken ct = default)
    {
        var f = Builders<FeeScheduleChange>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(feeScheduleId))
            filter &= f.Eq(x => x.FeeScheduleId, feeScheduleId);
        if (since is { } from)
            filter &= f.Gte(x => x.ChangedAt, from);

        return await _changes.Find(filter)
            .SortByDescending(x => x.ChangedAt)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<FeeScheduleRevisionSnapshot?> GetRevisionAsync(
        string tenantId, string feeScheduleId, long revision, CancellationToken ct = default)
    {
        var f = Builders<FeeScheduleRevisionSnapshot>.Filter;
        return await _revisions
            .Find(f.Eq(x => x.Id, FeeScheduleRevisionSnapshot.MakeId(feeScheduleId, revision)) & f.Eq(x => x.TenantId, tenantId))
            .FirstOrDefaultAsync(ct);
    }
}
