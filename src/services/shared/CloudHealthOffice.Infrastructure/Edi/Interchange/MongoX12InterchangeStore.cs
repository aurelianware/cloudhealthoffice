using System.Collections.Concurrent;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// MongoDB store for interchange control. Four collections:
/// <list type="bullet">
/// <item><c>x12-interchange-receipts</c>: one document per (tenant, sender + receiver, ISA13), the duplicate check; TTL on <c>ReceivedAt</c>.</item>
/// <item><c>x12-control-sequences</c>: TA1 ISA13 sequences, one per (tenant, our id, partner).</item>
/// <item><c>x12-interchange-acknowledgments</c>: generated and received TA1s.</item>
/// <item><c>x12-outbound-interchanges</c>: interchanges CHO sent, with their TA1 status.</item>
/// </list>
/// Indexes are created once per database the first time the store writes to
/// it (so tenant-scoped databases, <c>MongoDb:UseTenantScoping</c>, get them
/// too), and at startup for the default database
/// (<see cref="X12InterchangeIndexInitializer"/>).
/// </summary>
public sealed class MongoX12InterchangeStore : IX12InterchangeStore
{
    public const string ReceiptsCollection = "x12-interchange-receipts";
    public const string SequencesCollection = "x12-control-sequences";
    public const string AcknowledgmentsCollection = "x12-interchange-acknowledgments";
    public const string OutboundCollection = "x12-outbound-interchanges";
    public const string ReceiptTtlIndexName = "receivedAt_ttl";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> IndexedDatabases = new();

    private readonly IMongoDatabase _database;
    private readonly TimeSpan _receiptTtl;
    private readonly IMongoCollection<InterchangeReceiptDocument> _receipts;
    private readonly IMongoCollection<ControlSequenceDocument> _sequences;
    private readonly IMongoCollection<InterchangeAcknowledgmentRecord> _acks;
    private readonly IMongoCollection<OutboundInterchangeRecord> _outbound;

    /// <param name="receiptTtl">How long receipts are kept (the duplicate window plus a day). Default 366 days.</param>
    public MongoX12InterchangeStore(IMongoDatabase database, TimeSpan? receiptTtl = null)
    {
        _database = database;
        _receiptTtl = receiptTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(366);
        _receipts = database.GetCollection<InterchangeReceiptDocument>(ReceiptsCollection);
        _sequences = database.GetCollection<ControlSequenceDocument>(SequencesCollection);
        _acks = database.GetCollection<InterchangeAcknowledgmentRecord>(AcknowledgmentsCollection);
        _outbound = database.GetCollection<OutboundInterchangeRecord>(OutboundCollection);
    }

    private Task EnsureIndexesOnceAsync(CancellationToken ct)
    {
        var key = $"{_database.Client.Settings.Server}|{_database.DatabaseNamespace.DatabaseName}";
        var lazy = IndexedDatabases.GetOrAdd(key, _ => new Lazy<Task>(() => EnsureIndexesAsync(CancellationToken.None)));
        return lazy.Value.IsFaulted ? RetryIndexes(key) : lazy.Value;

        Task RetryIndexes(string k)
        {
            IndexedDatabases.TryRemove(k, out _);
            return EnsureIndexesAsync(ct);
        }
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _acks.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<InterchangeAcknowledgmentRecord>(
                Builders<InterchangeAcknowledgmentRecord>.IndexKeys.Ascending(x => x.TenantId).Descending(x => x.CreatedAt)),
            new CreateIndexModel<InterchangeAcknowledgmentRecord>(
                Builders<InterchangeAcknowledgmentRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.AcknowledgedControlNumber)),
        ], ct);
        await _outbound.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OutboundInterchangeRecord>(
                Builders<OutboundInterchangeRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.ControlNumber)),
            new CreateIndexModel<OutboundInterchangeRecord>(
                Builders<OutboundInterchangeRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.AckStatus).Descending(x => x.SentAt)),
            new CreateIndexModel<OutboundInterchangeRecord>(
                Builders<OutboundInterchangeRecord>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.TransactionType).Ascending(x => x.SourceReference)),
        ], ct);

        // Receipts older than the window no longer block anything: let Mongo delete them.
        var ttl = new CreateIndexModel<InterchangeReceiptDocument>(
            Builders<InterchangeReceiptDocument>.IndexKeys.Ascending(x => x.ReceivedAt),
            new CreateIndexOptions { Name = ReceiptTtlIndexName, ExpireAfter = _receiptTtl });
        try
        {
            await _receipts.Indexes.CreateOneAsync(ttl, cancellationToken: ct);
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // The window changed since the index was made: replace it with the new expiry.
            await _receipts.Indexes.DropOneAsync(ReceiptTtlIndexName, ct);
            await _receipts.Indexes.CreateOneAsync(ttl, cancellationToken: ct);
        }
    }

    public async Task<bool> TryRegisterReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default)
    {
        await EnsureIndexesOnceAsync(ct);
        var id = ReceiptId(tenantId, partyKey, controlNumber);
        var cutoff = receivedAt - window;
        var f = Builders<InterchangeReceiptDocument>.Filter;

        // Matches an existing receipt only when it is older than the window
        // (then it is refreshed). A recent receipt does not match, so the
        // upsert tries to insert a second document with the same _id and the
        // unique _id index refuses it: that is the duplicate.
        var filter = f.Eq(x => x.Id, id) & f.Lt(x => x.ReceivedAt, cutoff);
        var update = Builders<InterchangeReceiptDocument>.Update
            .Set(x => x.ReceivedAt, receivedAt)
            .SetOnInsert(x => x.TenantId, tenantId)
            .SetOnInsert(x => x.PartyKey, partyKey)
            .SetOnInsert(x => x.ControlNumber, controlNumber);
        try
        {
            await _receipts.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task ReleaseReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, CancellationToken ct = default)
    {
        var id = ReceiptId(tenantId, partyKey, controlNumber);
        return _receipts.DeleteOneAsync(x => x.Id == id && x.TenantId == tenantId && x.ReceivedAt == receivedAt, ct);
    }

    public async Task<long> NextControlNumberAsync(string tenantId, string sequenceKey, CancellationToken ct = default)
    {
        var id = $"{tenantId}|{sequenceKey}";
        var doc = await _sequences.FindOneAndUpdateAsync(
            Builders<ControlSequenceDocument>.Filter.Eq(x => x.Id, id),
            Builders<ControlSequenceDocument>.Update.Inc(x => x.Value, 1L).SetOnInsert(x => x.TenantId, tenantId),
            new FindOneAndUpdateOptions<ControlSequenceDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);
        return X12ControlNumberRange.Wrap(doc.Value);
    }

    public async Task SaveAcknowledgmentAsync(InterchangeAcknowledgmentRecord record, CancellationToken ct = default)
    {
        await EnsureIndexesOnceAsync(ct);
        await _acks.ReplaceOneAsync(x => x.Id == record.Id, record, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task<InterchangeAcknowledgmentRecord?> GetAcknowledgmentAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _acks.Find(x => x.TenantId == tenantId && x.Id == id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<InterchangeAcknowledgmentRecord>> ListAcknowledgmentsAsync(string tenantId, InterchangeAcknowledgmentQuery query, CancellationToken ct = default)
    {
        var f = Builders<InterchangeAcknowledgmentRecord>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (query.Direction is not null) filter &= f.Eq(x => x.Direction, query.Direction);
        if (query.AckCode is not null) filter &= f.Eq(x => x.AckCode, query.AckCode);
        if (query.AcknowledgedControlNumber is not null) filter &= f.Eq(x => x.AcknowledgedControlNumber, query.AcknowledgedControlNumber);
        if (query.SenderId is not null) filter &= f.Eq(x => x.SenderId, query.SenderId);

        return await _acks.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(Math.Clamp(query.Limit, 1, 500))
            .ToListAsync(ct);
    }

    public async Task SaveOutboundAsync(OutboundInterchangeRecord record, CancellationToken ct = default)
    {
        await EnsureIndexesOnceAsync(ct);
        await _outbound.ReplaceOneAsync(x => x.Id == record.Id, record, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task<IReadOnlyList<OutboundInterchangeRecord>> FindOutboundAsync(string tenantId, string controlNumber, CancellationToken ct = default) =>
        await _outbound.Find(x => x.TenantId == tenantId && x.ControlNumber == controlNumber)
            .SortByDescending(x => x.SentAt)
            .Limit(50)
            .ToListAsync(ct);

    public async Task<OutboundInterchangeRecord?> FindOutboundBySourceAsync(string tenantId, string transactionType, string sourceReference, CancellationToken ct = default) =>
        await _outbound.Find(x => x.TenantId == tenantId && x.TransactionType == transactionType && x.SourceReference == sourceReference)
            .SortByDescending(x => x.SentAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<OutboundInterchangeRecord>> ListOutboundAsync(string tenantId, string? ackStatus, int limit, CancellationToken ct = default)
    {
        var f = Builders<OutboundInterchangeRecord>.Filter;
        var filter = f.Eq(x => x.TenantId, tenantId);
        if (ackStatus is not null) filter &= f.Eq(x => x.AckStatus, ackStatus);
        return await _outbound.Find(filter)
            .SortByDescending(x => x.SentAt)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
    }

    private static string ReceiptId(string tenantId, string partyKey, string controlNumber) => $"{tenantId}|{partyKey}|{controlNumber}";

    /// <summary>One received (tenant, sender + receiver, ISA13).</summary>
    [BsonIgnoreExtraElements]
    public sealed class InterchangeReceiptDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;

        /// <summary><c>ISA05:ISA06|ISA07:ISA08</c>.</summary>
        public string PartyKey { get; set; } = string.Empty;

        public string ControlNumber { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
    }

    /// <summary>A TA1 ISA13 sequence.</summary>
    public sealed class ControlSequenceDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public long Value { get; set; }
    }
}
