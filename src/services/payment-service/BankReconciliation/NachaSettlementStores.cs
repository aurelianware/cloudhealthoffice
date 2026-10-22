using System.Collections.Concurrent;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.BankReconciliation;

/// <summary>
/// A transmitted NACHA file the bank statement shows as settled. Kept apart
/// from <see cref="PaymentFileTransmission"/> (whose versioned state machine
/// belongs to transmission); one per (tenant, file reference), insert-only,
/// so a file is settled by one bank line at most.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class NachaFileSettlement
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string FileReference { get; set; } = string.Empty;
    public string PaymentRunId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime EffectiveEntryDate { get; set; }

    /// <summary>The bank statement line (and reconciliation item) that settled it.</summary>
    public string BankTransactionId { get; set; } = string.Empty;
    public string StatementId { get; set; } = string.Empty;
    public string ReconciliationItemId { get; set; } = string.Empty;
    public DateTime BankAsOfDate { get; set; }

    /// <summary>Automatic (matched) or the user who marked it.</summary>
    public string SettledBy { get; set; } = string.Empty;
    public DateTime SettledAt { get; set; }

    /// <summary>Inserted atomically with the settlement, recoverable after a crash.</summary>
    public List<PaymentFileOutboxMessage> GlOutbox { get; set; } = new();
    public string? GlAccountingLimitation { get; set; }

    public static string KeyFor(string tenantId, string fileReference) =>
        $"settle:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(fileReference)}";
}

public interface INachaSettlementStore
{
    /// <summary>False when the file is already settled.</summary>
    Task<bool> TryInsertAsync(NachaFileSettlement settlement, CancellationToken ct = default);

    Task<NachaFileSettlement?> GetAsync(string tenantId, string fileReference, CancellationToken ct = default);
}

/// <summary>
/// Transmitted NACHA files a bank line may settle. Read-only over the
/// transmission records, so transmission code is untouched.
/// </summary>
public interface INachaSettlementCandidateSource
{
    /// <summary>Files in <see cref="PaymentFileTransmissionStatus.Transmitted"/> with an effective entry date in [from, to].</summary>
    Task<IReadOnlyList<PaymentFileTransmission>> TransmittedAsync(string tenantId, DateTime from, DateTime to, CancellationToken ct = default);
}

public sealed class NachaSettlementStoreMongo : INachaSettlementStore
{
    public const string CollectionName = "NachaFileSettlements";
    private readonly IMongoCollection<NachaFileSettlement> _collection;

    public NachaSettlementStoreMongo(IMongoDatabase database) =>
        _collection = database.GetCollection<NachaFileSettlement>(CollectionName);

    public async Task<bool> TryInsertAsync(NachaFileSettlement settlement, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(settlement, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<NachaFileSettlement?> GetAsync(string tenantId, string fileReference, CancellationToken ct = default) =>
        await _collection.Find(x => x.Id == NachaFileSettlement.KeyFor(tenantId, fileReference) && x.TenantId == tenantId).FirstOrDefaultAsync(ct);
}

public sealed class NachaSettlementCandidateSourceMongo : INachaSettlementCandidateSource
{
    private readonly IMongoCollection<PaymentFileTransmission> _collection;

    public NachaSettlementCandidateSourceMongo(IMongoDatabase database) =>
        _collection = database.GetCollection<PaymentFileTransmission>(PaymentFileTransmissionRepositoryMongo.CollectionName);

    public async Task<IReadOnlyList<PaymentFileTransmission>> TransmittedAsync(string tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var f = Builders<PaymentFileTransmission>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId)
                     & f.Eq(x => x.Status, PaymentFileTransmissionStatus.Transmitted)
                     & f.Gte(x => x.EffectiveEntryDate, from)
                     & f.Lte(x => x.EffectiveEntryDate, to);
        return await _collection.Find(filter).Limit(500).ToListAsync(ct);
    }
}

/// <summary>In-memory stores, for tests and hosts without MongoDB.</summary>
public sealed class InMemoryNachaSettlementStore : INachaSettlementStore, INachaSettlementCandidateSource
{
    private readonly ConcurrentDictionary<string, NachaFileSettlement> _settlements = new();

    public List<PaymentFileTransmission> Transmissions { get; } = [];

    public Task<bool> TryInsertAsync(NachaFileSettlement settlement, CancellationToken ct = default) =>
        Task.FromResult(_settlements.TryAdd(settlement.Id, settlement));

    public Task<NachaFileSettlement?> GetAsync(string tenantId, string fileReference, CancellationToken ct = default) =>
        Task.FromResult(_settlements.TryGetValue(NachaFileSettlement.KeyFor(tenantId, fileReference), out var s) ? s : null);

    public Task<IReadOnlyList<PaymentFileTransmission>> TransmittedAsync(string tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        lock (Transmissions)
            return Task.FromResult<IReadOnlyList<PaymentFileTransmission>>(Transmissions
                .Where(t => t.TenantId == tenantId && t.Status == PaymentFileTransmissionStatus.Transmitted
                            && t.EffectiveEntryDate >= from && t.EffectiveEntryDate <= to)
                .ToList());
    }
}
