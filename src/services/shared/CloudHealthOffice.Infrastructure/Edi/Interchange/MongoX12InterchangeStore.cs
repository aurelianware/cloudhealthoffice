using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// MongoDB store for interchange control. Three collections:
/// <list type="bullet">
/// <item><c>x12-interchange-receipts</c>: one document per (tenant, sender, ISA13), the duplicate check.</item>
/// <item><c>x12-interchange-acknowledgments</c>: generated and received TA1s.</item>
/// <item><c>x12-outbound-interchanges</c>: interchanges CHO sent, with their TA1 outcome.</item>
/// </list>
/// Indexes are created by <see cref="EnsureIndexesAsync"/> (run once at
/// startup by <see cref="X12InterchangeIndexInitializer"/>).
/// </summary>
public sealed class MongoX12InterchangeStore : IX12InterchangeStore
{
    public const string ReceiptsCollection = "x12-interchange-receipts";
    public const string AcknowledgmentsCollection = "x12-interchange-acknowledgments";
    public const string OutboundCollection = "x12-outbound-interchanges";

    private readonly IMongoCollection<InterchangeReceiptDocument> _receipts;
    private readonly IMongoCollection<InterchangeAcknowledgmentRecord> _acks;
    private readonly IMongoCollection<OutboundInterchangeRecord> _outbound;

    public MongoX12InterchangeStore(IMongoDatabase database)
    {
        _receipts = database.GetCollection<InterchangeReceiptDocument>(ReceiptsCollection);
        _acks = database.GetCollection<InterchangeAcknowledgmentRecord>(AcknowledgmentsCollection);
        _outbound = database.GetCollection<OutboundInterchangeRecord>(OutboundCollection);
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
        ], ct);
    }

    public async Task<bool> TryRegisterReceiptAsync(string tenantId, string senderKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default)
    {
        var id = $"{tenantId}|{senderKey}|{controlNumber}";
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
            .SetOnInsert(x => x.SenderKey, senderKey)
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

    public Task SaveAcknowledgmentAsync(InterchangeAcknowledgmentRecord record, CancellationToken ct = default) =>
        _acks.ReplaceOneAsync(x => x.Id == record.Id, record, new ReplaceOptions { IsUpsert = true }, ct);

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

    public Task SaveOutboundAsync(OutboundInterchangeRecord record, CancellationToken ct = default) =>
        _outbound.ReplaceOneAsync(x => x.Id == record.Id, record, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<OutboundInterchangeRecord>> FindOutboundAsync(string tenantId, string controlNumber, CancellationToken ct = default) =>
        await _outbound.Find(x => x.TenantId == tenantId && x.ControlNumber == controlNumber)
            .SortByDescending(x => x.SentAt)
            .Limit(50)
            .ToListAsync(ct);

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

    /// <summary>One received (tenant, sender, ISA13).</summary>
    public sealed class InterchangeReceiptDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string SenderKey { get; set; } = string.Empty;
        public string ControlNumber { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
    }
}
