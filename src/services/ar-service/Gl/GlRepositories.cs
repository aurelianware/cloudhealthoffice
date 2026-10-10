using ArService.Models;
using ArService.Repositories;
using MongoDB.Driver;

namespace ArService.Gl;

/// <summary>
/// The journal: insert and read, nothing else. There is no update or delete, so a posted
/// entry can only be corrected by a reversing entry. Every call names the tenant.
/// </summary>
public interface IGlJournalRepository
{
    /// <summary>Inserts the entry if no entry has its id. False when one exists (the caller decides whose it is).</summary>
    Task<bool> TryInsertAsync(GlJournalEntry entry, CancellationToken cancellationToken = default);
    Task<GlJournalEntry?> GetAsync(string tenantId, string entryId, CancellationToken cancellationToken = default);

    /// <summary>Entries of a period (or all, when null), ordered by entry date then id. Read in pages.</summary>
    Task<IReadOnlyList<GlJournalEntry>> ListAsync(string tenantId, string? period, CancellationToken cancellationToken = default);

    /// <summary>One page, ordered by id: the entries after <paramref name="afterId"/> (null: from the start), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<GlJournalEntry>> ListPageAsync(string tenantId, string? period, string? afterId, int limit, CancellationToken cancellationToken = default);
}

public interface IGlSourceEventRepository
{
    Task<bool> TryInsertAsync(GlSourceEvent record, CancellationToken cancellationToken = default);
    Task<GlSourceEvent?> GetAsync(string tenantId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the record while its version is <paramref name="expectedVersion"/>; sets a new version.</summary>
    Task<bool> TryReplaceAsync(GlSourceEvent record, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Every record (of a status), ordered by received time then event id. Read in pages.</summary>
    Task<IReadOnlyList<GlSourceEvent>> ListAsync(string tenantId, GlSourceEventStatus? status, CancellationToken cancellationToken = default);

    /// <summary>One page, ordered by id: the records after <paramref name="afterId"/> (null: from the start), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<GlSourceEvent>> ListPageAsync(string tenantId, GlSourceEventStatus? status, string? afterId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Parked records waiting for <paramref name="eventId"/> (<see cref="GlSourceEvent.WaitingForEventId"/>).</summary>
    Task<IReadOnlyList<GlSourceEvent>> ListWaitingForAsync(string tenantId, string eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Periods and the serialization of posts with the close (see <see cref="GlPeriod"/>):
/// <see cref="TryBeginPostAsync"/> holds a lease while the period is open,
/// <see cref="TryCloseAsync"/> closes only while no live lease is held.
/// </summary>
public interface IGlPeriodRepository
{
    Task<GlPeriod?> GetAsync(string tenantId, string period, CancellationToken cancellationToken = default);

    /// <summary>Reserves <paramref name="token"/> in the period while it is open (creating it). False when it is closed.</summary>
    Task<bool> TryBeginPostAsync(string tenantId, string period, string token, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Releases the lease (idempotent).</summary>
    Task EndPostAsync(string tenantId, string period, string token, CancellationToken cancellationToken = default);

    /// <summary>Closes the period in one conditional write: only while open and no lease expires after <paramref name="now"/>.</summary>
    Task<GlPeriodCloseOutcome> TryCloseAsync(string tenantId, string period, DateTime now, string closedBy, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Records the closed period's extract control, once (false when one is already recorded).</summary>
    Task<bool> TrySetExtractControlAsync(string tenantId, string period, GlExtractControl control, CancellationToken cancellationToken = default);

    /// <summary>The tenant's periods (open ones only once something posted into them), ordered by period.</summary>
    Task<IReadOnlyList<GlPeriod>> ListAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>The tenant's chart of accounts (ar-service <c>gl_accounts</c>), by account number.</summary>
public interface IGlChartLookup
{
    Task<GlAccount?> FindAsync(string tenantId, string accountNumber, CancellationToken cancellationToken = default);
}

internal static class GlPaging
{
    public const int PageSize = 1000;
    public const int MaxLimit = 5000;

    public static int Clamp(int limit) => Math.Clamp(limit, 1, MaxLimit);

    /// <summary>Reads every page of <paramref name="page"/> (ordered by id), <see cref="PageSize"/> at a time.</summary>
    public static async Task<List<T>> ReadAllAsync<T>(Func<string?, int, Task<IReadOnlyList<T>>> page, Func<T, string> id)
    {
        var all = new List<T>();
        string? after = null;
        while (true)
        {
            var batch = await page(after, PageSize);
            all.AddRange(batch);
            if (batch.Count < PageSize)
                return all;
            after = id(batch[^1]);
        }
    }
}

public sealed class MongoGlJournalRepository : IGlJournalRepository
{
    public const string CollectionName = "gl_journal_entries";
    private readonly IMongoCollection<GlJournalEntry> _collection;

    // Constructing a repository never touches the database (GL off means no GL I/O);
    // indexes are created on the first write.
    public MongoGlJournalRepository(IMongoDatabase database)
        => _collection = database.GetCollection<GlJournalEntry>(CollectionName);

    private void EnsureIndexes()
        => IndexGuard.EnsureOnce(CollectionName, () => _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<GlJournalEntry>(Builders<GlJournalEntry>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Period).Ascending(x => x.Id)),
            new CreateIndexModel<GlJournalEntry>(Builders<GlJournalEntry>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.SourceDocumentId)),
        }));

    public async Task<bool> TryInsertAsync(GlJournalEntry entry, CancellationToken cancellationToken = default)
    {
        GlJournal.EnsureBalanced(entry);
        EnsureIndexes();
        try
        {
            await _collection.InsertOneAsync(entry, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<GlJournalEntry?> GetAsync(string tenantId, string entryId, CancellationToken cancellationToken = default)
        => await _collection.Find(x => x.Id == entryId && x.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<GlJournalEntry>> ListAsync(string tenantId, string? period, CancellationToken cancellationToken = default)
    {
        var all = await GlPaging.ReadAllAsync((after, size) => ListPageAsync(tenantId, period, after, size, cancellationToken), e => e.Id);
        return all.OrderBy(e => e.EntryDate).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<GlJournalEntry>> ListPageAsync(string tenantId, string? period, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var f = Builders<GlJournalEntry>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (period != null)
            filter &= f.Eq(x => x.Period, period);
        if (afterId != null)
            filter &= f.Gt(x => x.Id, afterId);
        return await _collection.Find(filter).Sort(Builders<GlJournalEntry>.Sort.Ascending(x => x.Id)).Limit(GlPaging.Clamp(limit)).ToListAsync(cancellationToken);
    }
}

public sealed class MongoGlSourceEventRepository : IGlSourceEventRepository
{
    public const string CollectionName = "gl_source_events";
    private readonly IMongoCollection<GlSourceEvent> _collection;

    public MongoGlSourceEventRepository(IMongoDatabase database)
        => _collection = database.GetCollection<GlSourceEvent>(CollectionName);

    public async Task<bool> TryInsertAsync(GlSourceEvent record, CancellationToken cancellationToken = default)
    {
        IndexGuard.EnsureOnce(CollectionName, () => _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<GlSourceEvent>(Builders<GlSourceEvent>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Status).Ascending(x => x.Id)),
            new CreateIndexModel<GlSourceEvent>(Builders<GlSourceEvent>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.WaitingForEventId)),
        }));
        record.Id = GlSourceEvent.KeyFor(record.TenantId, record.EventId);
        record.Version = Guid.NewGuid().ToString("N");
        try
        {
            await _collection.InsertOneAsync(record, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<GlSourceEvent?> GetAsync(string tenantId, string eventId, CancellationToken cancellationToken = default)
    {
        var id = GlSourceEvent.KeyFor(tenantId, eventId);
        return await _collection.Find(x => x.Id == id && x.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryReplaceAsync(GlSourceEvent record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var previous = record.Version;
        record.Version = Guid.NewGuid().ToString("N");
        var result = await _collection.ReplaceOneAsync(
            x => x.Id == record.Id && x.TenantId == record.TenantId && x.Version == expectedVersion, record,
            cancellationToken: cancellationToken);
        if (result.MatchedCount == 1)
            return true;
        record.Version = previous;
        return false;
    }

    public async Task<IReadOnlyList<GlSourceEvent>> ListAsync(string tenantId, GlSourceEventStatus? status, CancellationToken cancellationToken = default)
    {
        var all = await GlPaging.ReadAllAsync((after, size) => ListPageAsync(tenantId, status, after, size, cancellationToken), e => e.Id);
        return all.OrderBy(e => e.ReceivedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<GlSourceEvent>> ListWaitingForAsync(string tenantId, string eventId, CancellationToken cancellationToken = default)
        => await _collection.Find(x => x.TenantId == tenantId && x.WaitingForEventId == eventId && x.Status == GlSourceEventStatus.Parked)
            .Limit(GlPaging.PageSize).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GlSourceEvent>> ListPageAsync(string tenantId, GlSourceEventStatus? status, string? afterId, int limit, CancellationToken cancellationToken = default)
    {
        var f = Builders<GlSourceEvent>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (status != null)
            filter &= f.Eq(x => x.Status, status.Value);
        if (afterId != null)
            filter &= f.Gt(x => x.Id, afterId);
        return await _collection.Find(filter).Sort(Builders<GlSourceEvent>.Sort.Ascending(x => x.Id)).Limit(GlPaging.Clamp(limit)).ToListAsync(cancellationToken);
    }
}

public sealed class MongoGlPeriodRepository : IGlPeriodRepository
{
    public const string CollectionName = "gl_periods";
    private readonly IMongoCollection<GlPeriod> _collection;

    public MongoGlPeriodRepository(IMongoDatabase database)
        => _collection = database.GetCollection<GlPeriod>(CollectionName);

    public async Task<GlPeriod?> GetAsync(string tenantId, string period, CancellationToken cancellationToken = default)
    {
        var id = GlPeriod.KeyFor(tenantId, period);
        return await _collection.Find(x => x.Id == id && x.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryBeginPostAsync(string tenantId, string period, string token, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var id = GlPeriod.KeyFor(tenantId, period);
        var f = Builders<GlPeriod>.Filter;
        var filter = f.Eq(x => x.Id, id) & f.Eq(x => x.TenantId, tenantId) & f.Ne(x => x.Status, GlPeriodStatus.Closed);
        var update = Builders<GlPeriod>.Update
            .Push(x => x.Leases, new GlPostLease { Token = token, ExpiresAt = expiresAt })
            .SetOnInsert(x => x.Period, period)
            .SetOnInsert(x => x.Status, GlPeriodStatus.Open);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Matches only an open period; for a period never used it inserts it open.
                // A closed one does not match, so the upsert collides with it (duplicate key).
                await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
                return true;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Either closed, or two first posts raced to create it: look and try again.
                var current = await GetAsync(tenantId, period, cancellationToken);
                if (current?.Status == GlPeriodStatus.Closed)
                    return false;
            }
        }
        return false;
    }

    public Task EndPostAsync(string tenantId, string period, string token, CancellationToken cancellationToken = default)
    {
        var id = GlPeriod.KeyFor(tenantId, period);
        return _collection.UpdateOneAsync(x => x.Id == id && x.TenantId == tenantId,
            Builders<GlPeriod>.Update.PullFilter(x => x.Leases, l => l.Token == token), cancellationToken: cancellationToken);
    }

    public async Task<GlPeriodCloseOutcome> TryCloseAsync(string tenantId, string period, DateTime now, string closedBy, string? reason, CancellationToken cancellationToken = default)
    {
        var id = GlPeriod.KeyFor(tenantId, period);
        var f = Builders<GlPeriod>.Filter;
        var filter = f.Eq(x => x.Id, id) & f.Eq(x => x.TenantId, tenantId) & f.Ne(x => x.Status, GlPeriodStatus.Closed)
                     & f.Not(f.ElemMatch(x => x.Leases, l => l.ExpiresAt > now));
        var update = Builders<GlPeriod>.Update
            .Set(x => x.Status, GlPeriodStatus.Closed)
            .Set(x => x.ClosedAt, now)
            .Set(x => x.ClosedBy, closedBy)
            .Set(x => x.Reason, reason)
            .Set(x => x.Leases, new List<GlPostLease>())
            .SetOnInsert(x => x.Period, period);
        try
        {
            await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
            return GlPeriodCloseOutcome.Closed;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The period exists and did not match: closed already, or a post holds a live lease.
            var current = await GetAsync(tenantId, period, cancellationToken);
            return current?.Status == GlPeriodStatus.Closed ? GlPeriodCloseOutcome.AlreadyClosed : GlPeriodCloseOutcome.Busy;
        }
    }

    public async Task<bool> TrySetExtractControlAsync(string tenantId, string period, GlExtractControl control, CancellationToken cancellationToken = default)
    {
        var id = GlPeriod.KeyFor(tenantId, period);
        var result = await _collection.UpdateOneAsync(
            x => x.Id == id && x.TenantId == tenantId && x.Status == GlPeriodStatus.Closed && x.ExtractSha256 == null,
            Builders<GlPeriod>.Update
                .Set(x => x.ExtractSha256, control.Sha256)
                .Set(x => x.ExtractEntryCount, control.EntryCount)
                .Set(x => x.ExtractLineCount, control.LineCount)
                .Set(x => x.ExtractTotalDebit, control.TotalDebit)
                .Set(x => x.ExtractTotalCredit, control.TotalCredit),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<GlPeriod>> ListAsync(string tenantId, CancellationToken cancellationToken = default)
        => (await _collection.Find(x => x.TenantId == tenantId).Limit(GlPaging.MaxLimit).ToListAsync(cancellationToken))
            .OrderBy(p => p.Period, StringComparer.Ordinal).ToList();
}

public sealed class MongoGlChartLookup : IGlChartLookup
{
    private readonly IMongoCollection<GlAccount> _collection;

    public MongoGlChartLookup(IMongoDatabase database)
        => _collection = database.GetCollection<GlAccount>("gl_accounts");

    public async Task<GlAccount?> FindAsync(string tenantId, string accountNumber, CancellationToken cancellationToken = default)
    {
        var matches = await _collection.Find(x => x.TenantId == tenantId && x.AccountNumber == accountNumber).ToListAsync(cancellationToken);
        // Two accounts with one number is a chart error: never guess between them.
        return matches.Count == 1 ? matches[0] : null;
    }
}
