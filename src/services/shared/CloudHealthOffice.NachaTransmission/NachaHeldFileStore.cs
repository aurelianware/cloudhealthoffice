using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.NachaTransmission;

public enum NachaHeldFileStatus
{
    /// <summary>Not delivered; a platform admin must retrieve it or a second approver retry it.</summary>
    AwaitingRetrieval,

    /// <summary>A retry is sending it right now.</summary>
    Transmitting,

    /// <summary>A retry delivered it. The file itself is no longer kept.</summary>
    Transmitted,

    /// <summary>A platform admin retrieved it for manual delivery.</summary>
    Retrieved
}

/// <summary>One platform-admin retrieval, with the reason they gave.</summary>
public sealed class NachaRetrieval
{
    public string By { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A NACHA file that could not be sent, kept encrypted (<c>IFieldProtector</c>,
/// <c>enc:v1:</c>) for at most the retention period (7 days), then deleted
/// (Mongo TTL on <see cref="ExpiresAt"/>; reads also refuse it after expiry).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class NachaHeldFile
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string SourceService { get; set; } = string.Empty;
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>The file, encrypted. Never plaintext; dropped once the file was delivered.</summary>
    [JsonIgnore] public string? ProtectedContent { get; set; }

    public string Sha256 { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public int EntryCount { get; set; }
    public decimal TotalDebitAmount { get; set; }
    public decimal TotalCreditAmount { get; set; }
    public string? RunId { get; set; }
    public string? BatchId { get; set; }

    /// <summary>The user who released the payment: never the one who retrieves or retries it.</summary>
    public string ReleasedBy { get; set; } = string.Empty;

    /// <summary>Why it was not sent (safe to show).</summary>
    public string Reason { get; set; } = string.Empty;

    public NachaHeldFileStatus Status { get; set; } = NachaHeldFileStatus.AwaitingRetrieval;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastAttemptBy { get; set; }
    public NachaTransmissionReceipt? Receipt { get; set; }
    public List<NachaRetrieval> Retrievals { get; set; } = new();

    public static string KeyOf(string tenantId, string fileReference) => $"{tenantId}:{fileReference}";
}

/// <summary>
/// Where undelivered NACHA files wait. Every call names the tenant (from the
/// caller's token); a record of another tenant is never returned.
/// </summary>
public interface INachaHeldFileStore
{
    Task SaveAsync(NachaHeldFile file, CancellationToken cancellationToken = default);

    Task<NachaHeldFile?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default);

    /// <summary>Files of this service not yet delivered or retrieved (no content).</summary>
    Task<IReadOnlyList<NachaHeldFile>> ListOpenAsync(string tenantId, string sourceService, CancellationToken cancellationToken = default);

    /// <summary>AwaitingRetrieval to Transmitting, so two retries never both send. False when the state was not AwaitingRetrieval.</summary>
    Task<bool> TryClaimAsync(string tenantId, string fileReference, string by, DateTime at, CancellationToken cancellationToken = default);

    /// <summary>Transmitting back to AwaitingRetrieval after a failed retry.</summary>
    Task ReleaseClaimAsync(string tenantId, string fileReference, string reason, CancellationToken cancellationToken = default);

    /// <summary>Transmitting to Transmitted; the encrypted file is dropped.</summary>
    Task MarkTransmittedAsync(string tenantId, string fileReference, NachaTransmissionReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>AwaitingRetrieval or Retrieved to Retrieved, recording the retrieval. False in any other state.</summary>
    Task<bool> RecordRetrievalAsync(string tenantId, string fileReference, NachaRetrieval retrieval, CancellationToken cancellationToken = default);
}

public sealed class MongoNachaHeldFileStore : INachaHeldFileStore
{
    public const string CollectionName = "NachaHeldFiles";

    private static readonly ConcurrentDictionary<string, bool> Indexed = new();
    private readonly IMongoCollection<NachaHeldFile> _collection;

    public MongoNachaHeldFileStore(IMongoDatabase database)
    {
        _collection = database.GetCollection<NachaHeldFile>(CollectionName);
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var key = _collection.Database.DatabaseNamespace.DatabaseName;
        if (Indexed.ContainsKey(key)) return;
        await _collection.Indexes.CreateManyAsync(new[]
        {
            // Deleted by Mongo once ExpiresAt passes.
            new CreateIndexModel<NachaHeldFile>(Builders<NachaHeldFile>.IndexKeys.Ascending(f => f.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_expiresAt" }),
            new CreateIndexModel<NachaHeldFile>(Builders<NachaHeldFile>.IndexKeys
                .Ascending(f => f.TenantId).Ascending(f => f.SourceService).Ascending(f => f.Status),
                new CreateIndexOptions { Name = "tenant_service_status" }),
        }, cancellationToken);
        Indexed[key] = true;
    }

    private static FilterDefinition<NachaHeldFile> Key(string tenantId, string fileReference)
        => Builders<NachaHeldFile>.Filter.And(
            Builders<NachaHeldFile>.Filter.Eq(f => f.Id, NachaHeldFile.KeyOf(tenantId, fileReference)),
            Builders<NachaHeldFile>.Filter.Eq(f => f.TenantId, tenantId));

    public async Task SaveAsync(NachaHeldFile file, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        file.Id = NachaHeldFile.KeyOf(file.TenantId, file.FileReference);
        await _collection.InsertOneAsync(file, cancellationToken: cancellationToken);
    }

    public async Task<NachaHeldFile?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
        => await _collection.Find(Key(tenantId, fileReference)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<NachaHeldFile>> ListOpenAsync(string tenantId, string sourceService, CancellationToken cancellationToken = default)
    {
        var f = Builders<NachaHeldFile>.Filter;
        var filter = f.And(
            f.Eq(x => x.TenantId, tenantId),
            f.Eq(x => x.SourceService, sourceService),
            f.In(x => x.Status, new[] { NachaHeldFileStatus.AwaitingRetrieval, NachaHeldFileStatus.Transmitting }));
        var list = await _collection.Find(filter)
            .Project<NachaHeldFile>(Builders<NachaHeldFile>.Projection.Exclude(x => x.ProtectedContent))
            .SortByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        return list;
    }

    public async Task<bool> TryClaimAsync(string tenantId, string fileReference, string by, DateTime at, CancellationToken cancellationToken = default)
    {
        var filter = Builders<NachaHeldFile>.Filter.And(Key(tenantId, fileReference),
            Builders<NachaHeldFile>.Filter.Eq(x => x.Status, NachaHeldFileStatus.AwaitingRetrieval));
        var update = Builders<NachaHeldFile>.Update
            .Set(x => x.Status, NachaHeldFileStatus.Transmitting)
            .Set(x => x.LastAttemptAt, at)
            .Set(x => x.LastAttemptBy, by)
            .Inc(x => x.Attempts, 1);
        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public async Task ReleaseClaimAsync(string tenantId, string fileReference, string reason, CancellationToken cancellationToken = default)
    {
        var filter = Builders<NachaHeldFile>.Filter.And(Key(tenantId, fileReference),
            Builders<NachaHeldFile>.Filter.Eq(x => x.Status, NachaHeldFileStatus.Transmitting));
        await _collection.UpdateOneAsync(filter, Builders<NachaHeldFile>.Update
            .Set(x => x.Status, NachaHeldFileStatus.AwaitingRetrieval)
            .Set(x => x.Reason, reason), cancellationToken: cancellationToken);
    }

    public async Task MarkTransmittedAsync(string tenantId, string fileReference, NachaTransmissionReceipt receipt, CancellationToken cancellationToken = default)
    {
        var filter = Builders<NachaHeldFile>.Filter.And(Key(tenantId, fileReference),
            Builders<NachaHeldFile>.Filter.Eq(x => x.Status, NachaHeldFileStatus.Transmitting));
        await _collection.UpdateOneAsync(filter, Builders<NachaHeldFile>.Update
            .Set(x => x.Status, NachaHeldFileStatus.Transmitted)
            .Set(x => x.Receipt, receipt)
            .Set(x => x.ProtectedContent, null), cancellationToken: cancellationToken);
    }

    public async Task<bool> RecordRetrievalAsync(string tenantId, string fileReference, NachaRetrieval retrieval, CancellationToken cancellationToken = default)
    {
        var filter = Builders<NachaHeldFile>.Filter.And(Key(tenantId, fileReference),
            Builders<NachaHeldFile>.Filter.In(x => x.Status, new[] { NachaHeldFileStatus.AwaitingRetrieval, NachaHeldFileStatus.Retrieved }));
        var result = await _collection.UpdateOneAsync(filter, Builders<NachaHeldFile>.Update
            .Set(x => x.Status, NachaHeldFileStatus.Retrieved)
            .Push(x => x.Retrievals, retrieval), cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }
}

/// <summary>Development, Testing and tests: held files in memory (lost on restart).</summary>
public sealed class InMemoryNachaHeldFileStore : INachaHeldFileStore
{
    private readonly ConcurrentDictionary<string, NachaHeldFile> _files = new();
    private readonly object _gate = new();

    public IReadOnlyCollection<NachaHeldFile> All => _files.Values.ToList();

    public Task SaveAsync(NachaHeldFile file, CancellationToken cancellationToken = default)
    {
        file.Id = NachaHeldFile.KeyOf(file.TenantId, file.FileReference);
        if (!_files.TryAdd(file.Id, file))
            throw new InvalidOperationException($"A held NACHA file {file.FileReference} already exists.");
        return Task.CompletedTask;
    }

    public Task<NachaHeldFile?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
        => Task.FromResult(_files.TryGetValue(NachaHeldFile.KeyOf(tenantId, fileReference), out var f) && f.TenantId == tenantId ? f : null);

    public Task<IReadOnlyList<NachaHeldFile>> ListOpenAsync(string tenantId, string sourceService, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NachaHeldFile>>(_files.Values
            .Where(f => f.TenantId == tenantId && f.SourceService == sourceService
                        && f.Status is NachaHeldFileStatus.AwaitingRetrieval or NachaHeldFileStatus.Transmitting)
            .OrderByDescending(f => f.CreatedAt).ToList());

    public Task<bool> TryClaimAsync(string tenantId, string fileReference, string by, DateTime at, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(NachaHeldFile.KeyOf(tenantId, fileReference), out var f) || f.Status != NachaHeldFileStatus.AwaitingRetrieval)
                return Task.FromResult(false);
            f.Status = NachaHeldFileStatus.Transmitting;
            f.LastAttemptAt = at;
            f.LastAttemptBy = by;
            f.Attempts++;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseClaimAsync(string tenantId, string fileReference, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(NachaHeldFile.KeyOf(tenantId, fileReference), out var f) && f.Status == NachaHeldFileStatus.Transmitting)
            {
                f.Status = NachaHeldFileStatus.AwaitingRetrieval;
                f.Reason = reason;
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkTransmittedAsync(string tenantId, string fileReference, NachaTransmissionReceipt receipt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(NachaHeldFile.KeyOf(tenantId, fileReference), out var f) && f.Status == NachaHeldFileStatus.Transmitting)
            {
                f.Status = NachaHeldFileStatus.Transmitted;
                f.Receipt = receipt;
                f.ProtectedContent = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> RecordRetrievalAsync(string tenantId, string fileReference, NachaRetrieval retrieval, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(NachaHeldFile.KeyOf(tenantId, fileReference), out var f)
                || f.Status is not (NachaHeldFileStatus.AwaitingRetrieval or NachaHeldFileStatus.Retrieved))
                return Task.FromResult(false);
            f.Status = NachaHeldFileStatus.Retrieved;
            f.Retrievals.Add(retrieval);
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// No place to hold files (Cosmos native SDK deployments, or no database
/// outside Development): a file that cannot be sent is not kept, and the
/// payments stay Pending for the next release.
/// </summary>
public sealed class UnavailableNachaHeldFileStore : INachaHeldFileStore
{
    public const string Message = "No store for undelivered NACHA files is configured (MongoDb:ConnectionString).";

    public Task SaveAsync(NachaHeldFile file, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Message);
    public Task<NachaHeldFile?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default) => Task.FromResult<NachaHeldFile?>(null);
    public Task<IReadOnlyList<NachaHeldFile>> ListOpenAsync(string tenantId, string sourceService, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NachaHeldFile>>(Array.Empty<NachaHeldFile>());
    public Task<bool> TryClaimAsync(string tenantId, string fileReference, string by, DateTime at, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task ReleaseClaimAsync(string tenantId, string fileReference, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkTransmittedAsync(string tenantId, string fileReference, NachaTransmissionReceipt receipt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> RecordRetrievalAsync(string tenantId, string fileReference, NachaRetrieval retrieval, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
