using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// The provider receivable ledger store. Tenant-scoped by an explicit tenant
/// argument (a payment run passes its own tenant, as reservations do), and
/// every write is conditional on the record's <see cref="ProviderReceivableRecord.Version"/>.
/// </summary>
public interface IProviderReceivableRepository
{
    /// <summary>
    /// Inserts the record unless one with its id exists; returns the stored
    /// record either way (recording the same origin twice is a no-op).
    /// </summary>
    Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record);

    Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id);

    /// <summary>The provider's receivables with something still outstanding, oldest first.</summary>
    Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi);

    Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null);

    /// <summary>
    /// Replaces the record only while it still carries <paramref name="expectedVersion"/>
    /// (the caller has already incremented <see cref="ProviderReceivableRecord.Version"/>).
    /// False when another writer got there first.
    /// </summary>
    Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion);
}

public sealed class ProviderReceivableRepositoryMongo : IProviderReceivableRepository
{
    public const string CollectionName = "ProviderReceivables";

    private readonly IMongoCollection<ProviderReceivableRecord> _collection;

    public ProviderReceivableRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<ProviderReceivableRecord>(CollectionName);
    }

    public async Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record)
    {
        try
        {
            await _collection.InsertOneAsync(record);
            return record;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return (await GetAsync(record.TenantId, record.Id))
                   ?? throw new InvalidOperationException($"Receivable {record.Id} exists but could not be read back.");
        }
    }

    public async Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id)
    {
        var filter = Builders<ProviderReceivableRecord>.Filter.And(
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.Id, id),
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi)
    {
        // Status, not the amount, is filtered on: decimals are not compared in the store.
        var filter = Builders<ProviderReceivableRecord>.Filter.And(
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.ProviderNpi, providerNpi),
            Builders<ProviderReceivableRecord>.Filter.Ne(x => x.Status, ReceivableStatus.Recovered));
        var found = await _collection.Find(filter).ToListAsync();
        return ReceivableOrdering.OldestFirst(found);
    }

    public async Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null)
    {
        var filters = new List<FilterDefinition<ProviderReceivableRecord>>
        {
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.TenantId, tenantId),
        };
        if (!string.IsNullOrEmpty(providerNpi))
            filters.Add(Builders<ProviderReceivableRecord>.Filter.Eq(x => x.ProviderNpi, providerNpi));
        if (status.HasValue)
            filters.Add(Builders<ProviderReceivableRecord>.Filter.Eq(x => x.Status, status.Value));
        var found = await _collection.Find(Builders<ProviderReceivableRecord>.Filter.And(filters)).ToListAsync();
        return ReceivableOrdering.OldestFirst(found);
    }

    public async Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
    {
        var filter = Builders<ProviderReceivableRecord>.Filter.And(
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.Id, record.Id),
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.TenantId, record.TenantId),
            Builders<ProviderReceivableRecord>.Filter.Eq(x => x.Version, expectedVersion));
        var result = await _collection.ReplaceOneAsync(filter, record);
        return result.ModifiedCount == 1;
    }
}

/// <summary>
/// In-memory ledger for Cosmos-only (development) deployments, mirroring
/// <see cref="InMemoryEraEnvelopeRepository"/>: payment-service's canonical
/// store is Mongo, where the ledger is durable. Records are cloned in and out
/// so a caller's edits never reach the store without <see cref="TryReplaceAsync"/>.
/// </summary>
public sealed class InMemoryProviderReceivableRepository : IProviderReceivableRepository
{
    private readonly Dictionary<string, ProviderReceivableRecord> _records = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public IReadOnlyList<ProviderReceivableRecord> All
    {
        get { lock (_lock) return _records.Values.Select(Clone).ToList(); }
    }

    public Task<ProviderReceivableRecord> CreateIfAbsentAsync(ProviderReceivableRecord record)
    {
        lock (_lock)
        {
            var key = Key(record.TenantId, record.Id);
            if (!_records.TryGetValue(key, out var existing))
            {
                existing = Clone(record);
                _records[key] = existing;
            }
            return Task.FromResult(Clone(existing));
        }
    }

    public Task<ProviderReceivableRecord?> GetAsync(string tenantId, string id)
    {
        lock (_lock)
            return Task.FromResult(_records.TryGetValue(Key(tenantId, id), out var r) ? Clone(r) : null);
    }

    public Task<IReadOnlyList<ProviderReceivableRecord>> ListOutstandingAsync(string tenantId, string providerNpi)
    {
        lock (_lock)
            return Task.FromResult(ReceivableOrdering.OldestFirst(_records.Values
                .Where(r => r.TenantId == tenantId && r.ProviderNpi == providerNpi && r.Status != ReceivableStatus.Recovered)
                .Select(Clone)));
    }

    public Task<IReadOnlyList<ProviderReceivableRecord>> SearchAsync(string tenantId, string? providerNpi = null, ReceivableStatus? status = null)
    {
        lock (_lock)
            return Task.FromResult(ReceivableOrdering.OldestFirst(_records.Values
                .Where(r => r.TenantId == tenantId
                            && (string.IsNullOrEmpty(providerNpi) || r.ProviderNpi == providerNpi)
                            && (!status.HasValue || r.Status == status.Value))
                .Select(Clone)));
    }

    public Task<bool> TryReplaceAsync(ProviderReceivableRecord record, long expectedVersion)
    {
        lock (_lock)
        {
            var key = Key(record.TenantId, record.Id);
            if (!_records.TryGetValue(key, out var stored) || stored.Version != expectedVersion)
                return Task.FromResult(false);
            _records[key] = Clone(record);
            return Task.FromResult(true);
        }
    }

    private static string Key(string tenantId, string id) => tenantId + "\u0001" + id;

    private static ProviderReceivableRecord Clone(ProviderReceivableRecord r)
        => System.Text.Json.JsonSerializer.Deserialize<ProviderReceivableRecord>(System.Text.Json.JsonSerializer.Serialize(r))!;
}

internal static class ReceivableOrdering
{
    /// <summary>Oldest balance first (FIFO recovery), then id for a stable order.</summary>
    public static IReadOnlyList<ProviderReceivableRecord> OldestFirst(IEnumerable<ProviderReceivableRecord> records)
        => records.OrderBy(r => r.OriginatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
}
