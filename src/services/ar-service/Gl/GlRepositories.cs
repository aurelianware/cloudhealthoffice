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

    /// <summary>Entries of a period (or all, when null), ordered by entry date then id.</summary>
    Task<IReadOnlyList<GlJournalEntry>> ListAsync(string tenantId, string? period, CancellationToken cancellationToken = default);
}

public interface IGlSourceEventRepository
{
    Task<bool> TryInsertAsync(GlSourceEvent record, CancellationToken cancellationToken = default);
    Task<GlSourceEvent?> GetAsync(string tenantId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the record while its version is <paramref name="expectedVersion"/>; sets a new version.</summary>
    Task<bool> TryReplaceAsync(GlSourceEvent record, string expectedVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GlSourceEvent>> ListAsync(string tenantId, GlSourceEventStatus? status, CancellationToken cancellationToken = default);
}

public interface IGlPeriodRepository
{
    Task<bool> IsClosedAsync(string tenantId, string period, CancellationToken cancellationToken = default);

    /// <summary>Closes the period; false when it already was.</summary>
    Task<bool> TryCloseAsync(GlClosedPeriod period, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GlClosedPeriod>> ListAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>The tenant's chart of accounts (ar-service <c>gl_accounts</c>), by account number.</summary>
public interface IGlChartLookup
{
    Task<GlAccount?> FindAsync(string tenantId, string accountNumber, CancellationToken cancellationToken = default);
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
            new CreateIndexModel<GlJournalEntry>(Builders<GlJournalEntry>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Period)),
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
        var filter = Builders<GlJournalEntry>.Filter.Eq(x => x.TenantId, tenantId);
        if (period != null)
            filter &= Builders<GlJournalEntry>.Filter.Eq(x => x.Period, period);
        var list = await _collection.Find(filter).ToListAsync(cancellationToken);
        return list.OrderBy(e => e.EntryDate).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
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
        IndexGuard.EnsureOnce(CollectionName, () => _collection.Indexes.CreateOne(new CreateIndexModel<GlSourceEvent>(
            Builders<GlSourceEvent>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Status))));
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
        var filter = Builders<GlSourceEvent>.Filter.Eq(x => x.TenantId, tenantId);
        if (status != null)
            filter &= Builders<GlSourceEvent>.Filter.Eq(x => x.Status, status.Value);
        var list = await _collection.Find(filter).ToListAsync(cancellationToken);
        return list.OrderBy(e => e.ReceivedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();
    }
}

public sealed class MongoGlPeriodRepository : IGlPeriodRepository
{
    public const string CollectionName = "gl_closed_periods";
    private readonly IMongoCollection<GlClosedPeriod> _collection;

    public MongoGlPeriodRepository(IMongoDatabase database)
        => _collection = database.GetCollection<GlClosedPeriod>(CollectionName);

    public async Task<bool> IsClosedAsync(string tenantId, string period, CancellationToken cancellationToken = default)
    {
        var id = GlClosedPeriod.KeyFor(tenantId, period);
        return await _collection.Find(x => x.Id == id && x.TenantId == tenantId).AnyAsync(cancellationToken);
    }

    public async Task<bool> TryCloseAsync(GlClosedPeriod period, CancellationToken cancellationToken = default)
    {
        period.Id = GlClosedPeriod.KeyFor(period.TenantId, period.Period);
        try
        {
            await _collection.InsertOneAsync(period, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<GlClosedPeriod>> ListAsync(string tenantId, CancellationToken cancellationToken = default)
        => (await _collection.Find(x => x.TenantId == tenantId).ToListAsync(cancellationToken))
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
