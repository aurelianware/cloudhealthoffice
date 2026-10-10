using System.Collections.Concurrent;
using MongoDB.Driver;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>
/// Storage for BAI2 ingestion and reconciliation. Statement files are
/// insert-only. Transactions are insert-only, by stable id. A reconciliation
/// item is created once, and after that only changes through a conditional
/// update when a person resolves it or its event is published. Every read is
/// tenant-scoped.
/// </summary>
public interface IBankReconciliationStore
{
    /// <summary>
    /// Inserts the file. False when a file with the same id (same tenant and
    /// SHA-256) or, for an accepted file, the same bank file key is already stored.
    /// </summary>
    Task<bool> TryInsertFileAsync(BankStatementFile file, CancellationToken ct = default);

    Task<BankStatementFile?> GetFileAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>The accepted file with this bank file key, if any.</summary>
    Task<BankStatementFile?> FindAcceptedByBankFileKeyAsync(string tenantId, string bankFileKey, CancellationToken ct = default);

    Task<IReadOnlyList<BankStatementFile>> ListFilesAsync(string tenantId, int limit, CancellationToken ct = default);

    /// <summary>Inserts the transactions, skipping any already stored (same id).</summary>
    Task InsertTransactionsAsync(IReadOnlyList<BankTransaction> transactions, CancellationToken ct = default);

    Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(string tenantId, string statementId, CancellationToken ct = default);

    /// <summary>Creates the item unless one exists for the same service and transaction. False when it existed.</summary>
    Task<bool> TryCreateItemAsync(ReconciliationItem item, CancellationToken ct = default);

    Task<ReconciliationItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Replaces the item only while its stored version is <paramref name="expectedVersion"/>; sets a new version.</summary>
    Task<bool> TryUpdateItemAsync(ReconciliationItem item, string expectedVersion, CancellationToken ct = default);

    Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(string tenantId, string service, ReconciliationStatus? status, string? statementId, int limit, CancellationToken ct = default);
}

/// <summary>Process-local store, for tests.</summary>
public sealed class InMemoryBankReconciliationStore : IBankReconciliationStore
{
    private readonly ConcurrentDictionary<string, BankStatementFile> _files = new();
    private readonly ConcurrentDictionary<string, BankTransaction> _transactions = new();
    private readonly ConcurrentDictionary<string, ReconciliationItem> _items = new();
    private readonly object _gate = new();

    public Task<bool> TryInsertFileAsync(BankStatementFile file, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_files.ContainsKey(file.Id)) return Task.FromResult(false);
            if (file.BankFileKey is not null && _files.Values.Any(f => f.TenantId == file.TenantId && f.BankFileKey == file.BankFileKey))
                return Task.FromResult(false);
            _files[file.Id] = file;
            return Task.FromResult(true);
        }
    }

    public Task<BankStatementFile?> GetFileAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue(id, out var f) && f.TenantId == tenantId ? f : null);

    public Task<BankStatementFile?> FindAcceptedByBankFileKeyAsync(string tenantId, string bankFileKey, CancellationToken ct = default) =>
        Task.FromResult(_files.Values.FirstOrDefault(f => f.TenantId == tenantId && f.BankFileKey == bankFileKey));

    public Task<IReadOnlyList<BankStatementFile>> ListFilesAsync(string tenantId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankStatementFile>>(_files.Values.Where(f => f.TenantId == tenantId)
            .OrderByDescending(f => f.ReceivedAt).Take(Math.Clamp(limit, 1, 500)).ToList());

    public Task InsertTransactionsAsync(IReadOnlyList<BankTransaction> transactions, CancellationToken ct = default)
    {
        foreach (var t in transactions) _transactions.TryAdd(t.Id, t);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(string tenantId, string statementId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankTransaction>>(_transactions.Values
            .Where(t => t.TenantId == tenantId && t.StatementId == statementId).OrderBy(t => t.LineNumber).ToList());

    public Task<bool> TryCreateItemAsync(ReconciliationItem item, CancellationToken ct = default) =>
        Task.FromResult(_items.TryAdd(item.Id, Clone(item)));

    public Task<ReconciliationItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var i) && i.TenantId == tenantId ? Clone(i) : null);

    public Task<bool> TryUpdateItemAsync(ReconciliationItem item, string expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(item.Id, out var stored) || stored.Version != expectedVersion || stored.TenantId != item.TenantId)
                return Task.FromResult(false);
            item.Version = Guid.NewGuid().ToString("N");
            _items[item.Id] = Clone(item);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(string tenantId, string service, ReconciliationStatus? status, string? statementId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReconciliationItem>>(_items.Values
            .Where(i => i.TenantId == tenantId && i.Service == service && (status is null || i.Status == status) && (statementId is null || i.StatementId == statementId))
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Take(Math.Clamp(limit, 1, 500)).Select(Clone).ToList());

    private static ReconciliationItem Clone(ReconciliationItem i) => i.Copy();
}

/// <summary>
/// MongoDB store: <c>bank-statement-files</c> (insert-only; unique on
/// tenant + bank file key for accepted files), <c>bank-statement-transactions</c>
/// (insert-only) and <c>bank-reconciliation-items</c>.
/// </summary>
public sealed class MongoBankReconciliationStore : IBankReconciliationStore
{
    public const string FilesCollection = "bank-statement-files";
    public const string TransactionsCollection = "bank-statement-transactions";
    public const string ItemsCollection = "bank-reconciliation-items";

    private readonly IMongoCollection<BankStatementFile> _files;
    private readonly IMongoCollection<BankTransaction> _transactions;
    private readonly IMongoCollection<ReconciliationItem> _items;

    public MongoBankReconciliationStore(IMongoDatabase database)
    {
        _files = database.GetCollection<BankStatementFile>(FilesCollection);
        _transactions = database.GetCollection<BankTransaction>(TransactionsCollection);
        _items = database.GetCollection<ReconciliationItem>(ItemsCollection);
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _files.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BankStatementFile>(
                Builders<BankStatementFile>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.BankFileKey),
                new CreateIndexOptions<BankStatementFile>
                {
                    Unique = true,
                    Name = "tenant_bankFileKey_unique",
                    PartialFilterExpression = Builders<BankStatementFile>.Filter.Type(x => x.BankFileKey, MongoDB.Bson.BsonType.String),
                }),
            new CreateIndexModel<BankStatementFile>(
                Builders<BankStatementFile>.IndexKeys.Ascending(x => x.TenantId).Descending(x => x.ReceivedAt)),
        ], ct);
        await _transactions.Indexes.CreateOneAsync(new CreateIndexModel<BankTransaction>(
            Builders<BankTransaction>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.StatementId)), cancellationToken: ct);
        await _items.Indexes.CreateOneAsync(new CreateIndexModel<ReconciliationItem>(
            Builders<ReconciliationItem>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Service).Ascending(x => x.Status).Descending(x => x.CreatedAt)), cancellationToken: ct);
    }

    public async Task<bool> TryInsertFileAsync(BankStatementFile file, CancellationToken ct = default)
    {
        try
        {
            await _files.InsertOneAsync(file, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<BankStatementFile?> GetFileAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _files.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct);

    public async Task<BankStatementFile?> FindAcceptedByBankFileKeyAsync(string tenantId, string bankFileKey, CancellationToken ct = default) =>
        await _files.Find(x => x.TenantId == tenantId && x.BankFileKey == bankFileKey).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<BankStatementFile>> ListFilesAsync(string tenantId, int limit, CancellationToken ct = default) =>
        await _files.Find(x => x.TenantId == tenantId)
            .Project<BankStatementFile>(Builders<BankStatementFile>.Projection.Exclude(x => x.ProtectedContent))
            .SortByDescending(x => x.ReceivedAt).Limit(Math.Clamp(limit, 1, 500)).ToListAsync(ct);

    public async Task InsertTransactionsAsync(IReadOnlyList<BankTransaction> transactions, CancellationToken ct = default)
    {
        if (transactions.Count == 0) return;
        try
        {
            await _transactions.InsertManyAsync(transactions, new InsertManyOptions { IsOrdered = false }, ct);
        }
        catch (MongoBulkWriteException ex) when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            // Already stored by an earlier ingest of the same file.
        }
    }

    public async Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(string tenantId, string statementId, CancellationToken ct = default) =>
        await _transactions.Find(x => x.TenantId == tenantId && x.StatementId == statementId).SortBy(x => x.LineNumber).ToListAsync(ct);

    public async Task<bool> TryCreateItemAsync(ReconciliationItem item, CancellationToken ct = default)
    {
        try
        {
            await _items.InsertOneAsync(item, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<ReconciliationItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _items.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryUpdateItemAsync(ReconciliationItem item, string expectedVersion, CancellationToken ct = default)
    {
        var next = Guid.NewGuid().ToString("N");
        var previous = item.Version;
        item.Version = next;
        var result = await _items.ReplaceOneAsync(
            x => x.Id == item.Id && x.TenantId == item.TenantId && x.Version == expectedVersion, item, cancellationToken: ct);
        if (result.ModifiedCount == 1) return true;
        item.Version = previous;
        return false;
    }

    public async Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(string tenantId, string service, ReconciliationStatus? status, string? statementId, int limit, CancellationToken ct = default)
    {
        var f = Builders<ReconciliationItem>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId) & f.Eq(x => x.Service, service);
        if (status is not null) filter &= f.Eq(x => x.Status, status.Value);
        if (statementId is not null) filter &= f.Eq(x => x.StatementId, statementId);
        return await _items.Find(filter).SortByDescending(x => x.CreatedAt).Limit(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }
}

/// <summary>
/// Registered when the service runs without MongoDB (Cosmos-only). Bank
/// reconciliation needs insert-only, conditional storage, so it refuses
/// instead of falling back to memory, where idempotency would not survive a restart.
/// </summary>
public sealed class UnsupportedBankReconciliationStore : IBankReconciliationStore
{
    public const string Message = "BAI2 bank reconciliation requires MongoDB (MongoDb:ConnectionString); it is not available on Cosmos-only deployments.";

    private static Task<T> Refuse<T>() => Task.FromException<T>(new InvalidOperationException(Message));
    public Task<bool> TryInsertFileAsync(BankStatementFile file, CancellationToken ct = default) => Refuse<bool>();
    public Task<BankStatementFile?> GetFileAsync(string tenantId, string id, CancellationToken ct = default) => Refuse<BankStatementFile?>();
    public Task<BankStatementFile?> FindAcceptedByBankFileKeyAsync(string tenantId, string bankFileKey, CancellationToken ct = default) => Refuse<BankStatementFile?>();
    public Task<IReadOnlyList<BankStatementFile>> ListFilesAsync(string tenantId, int limit, CancellationToken ct = default) => Refuse<IReadOnlyList<BankStatementFile>>();
    public Task InsertTransactionsAsync(IReadOnlyList<BankTransaction> transactions, CancellationToken ct = default) => Refuse<bool>();
    public Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(string tenantId, string statementId, CancellationToken ct = default) => Refuse<IReadOnlyList<BankTransaction>>();
    public Task<bool> TryCreateItemAsync(ReconciliationItem item, CancellationToken ct = default) => Refuse<bool>();
    public Task<ReconciliationItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) => Refuse<ReconciliationItem?>();
    public Task<bool> TryUpdateItemAsync(ReconciliationItem item, string expectedVersion, CancellationToken ct = default) => Refuse<bool>();
    public Task<IReadOnlyList<ReconciliationItem>> ListItemsAsync(string tenantId, string service, ReconciliationStatus? status, string? statementId, int limit, CancellationToken ct = default) => Refuse<IReadOnlyList<ReconciliationItem>>();
}
