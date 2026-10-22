using System.Collections.Concurrent;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Edi.Files;

/// <summary>EDI file records. Every read and write is scoped to one tenant.</summary>
public interface IEdiFileStore
{
    Task CreateAsync(EdiFile file, CancellationToken ct = default);

    /// <summary>Replaces the tenant's file with this id.</summary>
    Task SaveAsync(EdiFile file, CancellationToken ct = default);

    Task<EdiFile?> GetAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>The tenant's files, newest first.</summary>
    Task<IReadOnlyList<EdiFile>> ListAsync(string tenantId, EdiFileQuery query, CancellationToken ct = default);
}

/// <summary>MongoDB store: collection <c>edi-files</c> in the service's own database.</summary>
public sealed class MongoEdiFileStore : IEdiFileStore
{
    public const string CollectionName = "edi-files";
    public const int MaxLimit = 500;

    private static readonly ConcurrentDictionary<string, Lazy<Task>> IndexedDatabases = new();

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<EdiFile> _files;

    public MongoEdiFileStore(IMongoDatabase database)
    {
        _database = database;
        _files = database.GetCollection<EdiFile>(CollectionName);
    }

    /// <summary>Creates the indexes (once per database per process).</summary>
    public Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var key = $"{_database.Client.Settings.Server}|{_database.DatabaseNamespace.DatabaseName}";
        var lazy = IndexedDatabases.GetOrAdd(key, _ => new Lazy<Task>(() => CreateIndexesAsync(CancellationToken.None)));
        if (!lazy.Value.IsFaulted) return lazy.Value;
        IndexedDatabases.TryRemove(key, out _);
        return CreateIndexesAsync(ct);
    }

    private Task CreateIndexesAsync(CancellationToken ct) =>
        _files.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<EdiFile>(Builders<EdiFile>.IndexKeys.Ascending(x => x.TenantId).Descending(x => x.ReceivedAt)),
            new CreateIndexModel<EdiFile>(Builders<EdiFile>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.InterchangeControlNumbers)),
        ], ct);

    public async Task CreateAsync(EdiFile file, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        await _files.InsertOneAsync(file, cancellationToken: ct);
    }

    public Task SaveAsync(EdiFile file, CancellationToken ct = default) =>
        _files.ReplaceOneAsync(x => x.TenantId == file.TenantId && x.Id == file.Id, file, cancellationToken: ct);

    public async Task<EdiFile?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _files.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EdiFile>> ListAsync(string tenantId, EdiFileQuery query, CancellationToken ct = default)
    {
        var f = Builders<EdiFile>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(query.Direction)) filter &= f.Eq(x => x.Direction, query.Direction);
        if (!string.IsNullOrWhiteSpace(query.TransactionType)) filter &= f.Eq(x => x.TransactionType, query.TransactionType);
        if (!string.IsNullOrWhiteSpace(query.Status)) filter &= f.Eq(x => x.Status, query.Status);
        if (query.From is { } from) filter &= f.Gte(x => x.ReceivedAt, from);
        if (query.To is { } to) filter &= f.Lt(x => x.ReceivedAt, to);
        if (!string.IsNullOrWhiteSpace(query.ControlNumber))
        {
            var n = query.ControlNumber.Trim();
            filter &= f.AnyEq(x => x.InterchangeControlNumbers, n)
                      | f.AnyEq(x => x.GroupControlNumbers, n)
                      | f.AnyEq(x => x.TransactionSetControlNumbers, n);
        }
        return await _files.Find(filter)
            .SortByDescending(x => x.ReceivedAt)
            .Limit(Math.Clamp(query.Limit, 1, MaxLimit))
            .ToListAsync(ct);
    }
}

/// <summary>Process-local store, for services without MongoDB and for tests.</summary>
public sealed class InMemoryEdiFileStore : IEdiFileStore
{
    private readonly ConcurrentDictionary<(string, string), EdiFile> _files = new();

    public Task CreateAsync(EdiFile file, CancellationToken ct = default)
    {
        _files[(file.TenantId, file.Id)] = file;
        return Task.CompletedTask;
    }

    public Task SaveAsync(EdiFile file, CancellationToken ct = default)
    {
        if (_files.ContainsKey((file.TenantId, file.Id))) _files[(file.TenantId, file.Id)] = file;
        return Task.CompletedTask;
    }

    public Task<EdiFile?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue((tenantId, id), out var file) ? file : null);

    public Task<IReadOnlyList<EdiFile>> ListAsync(string tenantId, EdiFileQuery query, CancellationToken ct = default)
    {
        var n = query.ControlNumber?.Trim();
        IReadOnlyList<EdiFile> list = _files.Values
            .Where(x => x.TenantId == tenantId)
            .Where(x => string.IsNullOrWhiteSpace(query.Direction) || x.Direction == query.Direction)
            .Where(x => string.IsNullOrWhiteSpace(query.TransactionType) || x.TransactionType == query.TransactionType)
            .Where(x => string.IsNullOrWhiteSpace(query.Status) || x.Status == query.Status)
            .Where(x => query.From is null || x.ReceivedAt >= query.From)
            .Where(x => query.To is null || x.ReceivedAt < query.To)
            .Where(x => string.IsNullOrWhiteSpace(n) || x.InterchangeControlNumbers.Contains(n)
                        || x.GroupControlNumbers.Contains(n) || x.TransactionSetControlNumbers.Contains(n))
            .OrderByDescending(x => x.ReceivedAt)
            .Take(Math.Clamp(query.Limit, 1, MongoEdiFileStore.MaxLimit))
            .ToList();
        return Task.FromResult(list);
    }
}
