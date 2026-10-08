namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using CloudHealthOffice.ProviderVerificationEngine.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

/// <summary>
/// MongoDB persistence for the local exclusion lists. The collections are
/// global reference data (not tenant-partitioned), shared by every replica.
/// </summary>
public sealed class MongoExclusionRecordStore : IExclusionRecordStore
{
    private const int BatchSize = 1000;

    private readonly IMongoCollection<ExclusionRecord> _records;
    private readonly IMongoCollection<ExclusionSyncStatusDocument> _status;

    static MongoExclusionRecordStore()
    {
        BsonClassMap.TryRegisterClassMap<ExclusionRecord>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(r => r.Id);
            cm.MapMember(r => r.Source).SetSerializer(new EnumSerializer<ExclusionScreeningSource>(BsonType.String));
        });
    }

    public MongoExclusionRecordStore(IMongoDatabase database, ExclusionScreeningOptions options)
    {
        _records = database.GetCollection<ExclusionRecord>(options.RecordsCollectionName);
        _status = database.GetCollection<ExclusionSyncStatusDocument>(options.SyncStatusCollectionName);
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var keys = Builders<ExclusionRecord>.IndexKeys;
        await _records.Indexes.CreateManyAsync(new[]
        {
            // Every lookup is scoped to the active sync snapshot, so the sync
            // id leads each lookup index.
            new CreateIndexModel<ExclusionRecord>(
                keys.Ascending(r => r.Source).Ascending(r => r.SyncId).Ascending(r => r.Npi),
                new CreateIndexOptions { Name = "source_sync_npi" }),
            new CreateIndexModel<ExclusionRecord>(
                keys.Ascending(r => r.Source).Ascending(r => r.SyncId).Ascending(r => r.NormalizedLastName).Ascending(r => r.DobKey),
                new CreateIndexOptions { Name = "source_sync_lastname_dob" }),
            new CreateIndexModel<ExclusionRecord>(
                keys.Ascending(r => r.Source).Ascending(r => r.SyncId).Ascending(r => r.NormalizedBusinessName),
                new CreateIndexOptions { Name = "source_sync_business_name" }),
            new CreateIndexModel<ExclusionRecord>(
                keys.Ascending(r => r.Source).Ascending(r => r.SyncId),
                new CreateIndexOptions { Name = "source_sync" })
        }, ct).ConfigureAwait(false);
    }

    public async Task<ExclusionDatasetLoadResult> ReplaceDatasetAsync(
        ExclusionScreeningSource source,
        IEnumerable<ExclusionRecord> records,
        ExclusionDatasetLoadPolicy policy,
        CancellationToken ct = default)
    {
        var syncId = Guid.NewGuid().ToString("N");
        var previous = (await GetSyncStatusAsync(source, ct).ConfigureAwait(false))?.RecordCount ?? 0;
        var count = 0;

        try
        {
            var batch = new List<ExclusionRecord>(BatchSize);
            foreach (var record in records)
            {
                record.Source = source;
                record.SyncId = syncId;
                batch.Add(record);
                if (batch.Count == BatchSize)
                {
                    await _records.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct).ConfigureAwait(false);
                    count += batch.Count;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await _records.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct).ConfigureAwait(false);
                count += batch.Count;
            }

            var rejection = ExclusionDatasetLoadChecks.Reject(count, previous, policy);
            if (rejection is not null)
            {
                await DeleteSyncAsync(source, syncId).ConfigureAwait(false);
                return new ExclusionDatasetLoadResult
                {
                    Succeeded = false, RecordCount = count, PreviousRecordCount = previous, Error = rejection
                };
            }

            var update = Builders<ExclusionSyncStatusDocument>.Update
                .Set(s => s.ActiveSyncId, syncId)
                .Set(s => s.LastSuccessfulSyncAt, policy.SyncedAt.UtcDateTime)
                .Set(s => s.LastAttemptAt, policy.SyncedAt.UtcDateTime)
                .Set(s => s.RecordCount, count)
                .Set(s => s.SourceUrl, policy.SourceUrl)
                .Set(s => s.LastError, null);
            await _status.UpdateOneAsync(s => s.Id == source.ToString(), update,
                new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Never leave a half-loaded copy behind; the previous dataset and
            // its sync timestamp are untouched.
            await DeleteSyncAsync(source, syncId).ConfigureAwait(false);
            throw;
        }

        // The new copy is live; drop every older copy of this source.
        var stale = Builders<ExclusionRecord>.Filter.And(
            Builders<ExclusionRecord>.Filter.Eq(r => r.Source, source),
            Builders<ExclusionRecord>.Filter.Ne(r => r.SyncId, syncId));
        await _records.DeleteManyAsync(stale, CancellationToken.None).ConfigureAwait(false);

        return new ExclusionDatasetLoadResult
        {
            Succeeded = true, RecordCount = count, PreviousRecordCount = previous, SyncId = syncId
        };
    }

    public Task<IReadOnlyList<ExclusionRecord>> FindByNpiAsync(ExclusionScreeningSource source, string syncId, string npi, CancellationToken ct = default) =>
        FindAsync(r => r.Source == source && r.SyncId == syncId && r.Npi == npi, ct);

    public Task<IReadOnlyList<ExclusionRecord>> FindByLastNameAsync(ExclusionScreeningSource source, string syncId, string normalizedLastName, CancellationToken ct = default) =>
        FindAsync(r => r.Source == source && r.SyncId == syncId && r.NormalizedLastName == normalizedLastName, ct);

    public Task<IReadOnlyList<ExclusionRecord>> FindByBusinessNameAsync(ExclusionScreeningSource source, string syncId, string normalizedBusinessName, CancellationToken ct = default) =>
        FindAsync(r => r.Source == source && r.SyncId == syncId && r.NormalizedBusinessName == normalizedBusinessName, ct);

    public async Task<ExclusionSyncStatus?> GetSyncStatusAsync(ExclusionScreeningSource source, CancellationToken ct = default)
    {
        var doc = await _status.Find(s => s.Id == source.ToString()).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel(source);
    }

    public Task RecordSyncFailureAsync(ExclusionScreeningSource source, string error, DateTimeOffset at, CancellationToken ct = default) =>
        _status.UpdateOneAsync(
            s => s.Id == source.ToString(),
            Builders<ExclusionSyncStatusDocument>.Update
                .Set(s => s.LastAttemptAt, at.UtcDateTime)
                .Set(s => s.LastError, error),
            new UpdateOptions { IsUpsert = true },
            ct);

    public async Task<bool> TryAcquireSyncLeaseAsync(ExclusionScreeningSource source, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct = default)
    {
        var f = Builders<ExclusionSyncStatusDocument>.Filter;
        var filter = f.And(
            f.Eq(s => s.Id, source.ToString()),
            f.Or(
                f.Eq(s => s.LeaseUntil, null),
                f.Lte(s => s.LeaseUntil, now.UtcDateTime),
                f.Eq(s => s.LeaseHolder, holder)));
        var update = Builders<ExclusionSyncStatusDocument>.Update
            .Set(s => s.LeaseHolder, holder)
            .Set(s => s.LeaseUntil, (now + duration).UtcDateTime);

        try
        {
            await _status.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The status document exists and another replica holds the lease.
            return false;
        }
    }

    public Task ReleaseSyncLeaseAsync(ExclusionScreeningSource source, string holder, CancellationToken ct = default) =>
        _status.UpdateOneAsync(
            s => s.Id == source.ToString() && s.LeaseHolder == holder,
            Builders<ExclusionSyncStatusDocument>.Update
                .Set(s => s.LeaseHolder, null)
                .Set(s => s.LeaseUntil, null),
            cancellationToken: ct);

    private async Task<IReadOnlyList<ExclusionRecord>> FindAsync(
        System.Linq.Expressions.Expression<Func<ExclusionRecord, bool>> filter, CancellationToken ct) =>
        // No limit: truncating candidates (e.g. a common last name) would be a silent false negative.
        await _records.Find(filter).ToListAsync(ct).ConfigureAwait(false);

    private Task DeleteSyncAsync(ExclusionScreeningSource source, string syncId) =>
        _records.DeleteManyAsync(r => r.Source == source && r.SyncId == syncId, CancellationToken.None);
}

/// <summary>Sync status as stored (UTC DateTime so lease comparisons work server-side).</summary>
[BsonIgnoreExtraElements]
internal sealed class ExclusionSyncStatusDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string? ActiveSyncId { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public int RecordCount { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? LeaseHolder { get; set; }
    public DateTime? LeaseUntil { get; set; }

    public ExclusionSyncStatus ToModel(ExclusionScreeningSource source) => new()
    {
        Source = source,
        ActiveSyncId = ActiveSyncId,
        LastSuccessfulSyncAt = ToOffset(LastSuccessfulSyncAt),
        RecordCount = RecordCount,
        SourceUrl = SourceUrl,
        LastAttemptAt = ToOffset(LastAttemptAt),
        LastError = LastError,
        LeaseHolder = LeaseHolder,
        LeaseUntil = ToOffset(LeaseUntil)
    };

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
