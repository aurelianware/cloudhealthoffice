using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Gateways.Persistence;

/// <summary>
/// MongoDB backing for transmissions, 277CA acknowledgments, outbox, and poll
/// cursors. Unique indexes make create-if-absent atomic across replicas.
/// </summary>
internal sealed class MongoClaimLifecycleStore :
    IClaimTransmissionStore,
    IClaimAcknowledgmentStore,
    IClaimAcknowledgmentCursorStore,
    IClaimAttachmentTransmissionStore,
    IInboundClaimAttachmentReceiptStore,
    IClaimStatusInquiryStore,
    IRemittanceStore
{
    private readonly IMongoCollection<ClaimTransmissionDocument> _transmissions;
    private readonly IMongoCollection<ClaimAcknowledgmentDocument> _acknowledgments;
    private readonly IMongoCollection<ClaimAcknowledgmentCursor> _cursors;
    private readonly IMongoCollection<ClaimAttachmentTransmissionDocument> _attachments;
    private readonly IMongoCollection<InboundClaimAttachmentReceiptDocument> _inboundAttachments;
    private readonly IMongoCollection<ClaimStatusInquiryDocument> _statusInquiries;
    private readonly IMongoCollection<RemittanceReceiptDocument> _remittances;

    public MongoClaimLifecycleStore(IMongoDatabase database, ClaimLifecycleOptions options)
    {
        _transmissions = database.GetCollection<ClaimTransmissionDocument>(options.TransmissionsCollection);
        _acknowledgments = database.GetCollection<ClaimAcknowledgmentDocument>(options.AcknowledgmentsCollection);
        _cursors = database.GetCollection<ClaimAcknowledgmentCursor>(options.CursorsCollection);
        _attachments = database.GetCollection<ClaimAttachmentTransmissionDocument>(options.AttachmentsCollection);
        _inboundAttachments = database.GetCollection<InboundClaimAttachmentReceiptDocument>(options.InboundAttachmentsCollection);
        _statusInquiries = database.GetCollection<ClaimStatusInquiryDocument>(options.ClaimStatusInquiriesCollection);
        _remittances = database.GetCollection<RemittanceReceiptDocument>(options.RemittancesCollection);
    }

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await _transmissions.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.GatewayName)
                    .Ascending(d => d.SubmissionId)),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.GatewayName)
                    .Ascending(d => d.ExternalTransactionId)),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.GatewayName)
                    .Ascending(d => d.PatientControlNumber)),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.GatewayName)
                    .Ascending(d => d.CorrelationId)),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending("Payload.ClaimId")),
            new CreateIndexModel<ClaimTransmissionDocument>(
                Builders<ClaimTransmissionDocument>.IndexKeys
                    .Ascending(d => d.GatewayName)
                    .Ascending("Payload.PayerClaimControlNumber"))
        }, ct).ConfigureAwait(false);

        await _acknowledgments.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ClaimAcknowledgmentDocument>(
                Builders<ClaimAcknowledgmentDocument>.IndexKeys.Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimAcknowledgmentDocument>(
                Builders<ClaimAcknowledgmentDocument>.IndexKeys.Ascending(d => d.EventKey),
                new CreateIndexOptions { Unique = true, Sparse = true }),
            new CreateIndexModel<ClaimAcknowledgmentDocument>(
                Builders<ClaimAcknowledgmentDocument>.IndexKeys.Ascending(d => d.HasPendingOutbox)),
            new CreateIndexModel<ClaimAcknowledgmentDocument>(
                Builders<ClaimAcknowledgmentDocument>.IndexKeys.Ascending(d => d.Payload.Status)),
            new CreateIndexModel<ClaimAcknowledgmentDocument>(
                Builders<ClaimAcknowledgmentDocument>.IndexKeys.Ascending(d => d.Payload.TransmissionId))
        }, ct).ConfigureAwait(false);

        await _cursors.Indexes.CreateOneAsync(
            new CreateIndexModel<ClaimAcknowledgmentCursor>(
                Builders<ClaimAcknowledgmentCursor>.IndexKeys.Ascending(d => d.GatewayName),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct).ConfigureAwait(false);

        await _attachments.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ClaimAttachmentTransmissionDocument>(
                Builders<ClaimAttachmentTransmissionDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimAttachmentTransmissionDocument>(
                Builders<ClaimAttachmentTransmissionDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimAttachmentTransmissionDocument>(
                Builders<ClaimAttachmentTransmissionDocument>.IndexKeys.Ascending(d => d.ClaimTransmissionId)),
            new CreateIndexModel<ClaimAttachmentTransmissionDocument>(
                Builders<ClaimAttachmentTransmissionDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.ChecksumSha256))
        }, ct).ConfigureAwait(false);

        await _inboundAttachments.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<InboundClaimAttachmentReceiptDocument>(
                Builders<InboundClaimAttachmentReceiptDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<InboundClaimAttachmentReceiptDocument>(
                Builders<InboundClaimAttachmentReceiptDocument>.IndexKeys.Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<InboundClaimAttachmentReceiptDocument>(
                Builders<InboundClaimAttachmentReceiptDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.ClaimId)),
            new CreateIndexModel<InboundClaimAttachmentReceiptDocument>(
                Builders<InboundClaimAttachmentReceiptDocument>.IndexKeys
                    .Ascending(d => d.HasPendingOutbox)
                    .Ascending("Payload.ReceivedAtUtc"))
        }, ct).ConfigureAwait(false);

        await _statusInquiries.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ClaimStatusInquiryDocument>(
                Builders<ClaimStatusInquiryDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimStatusInquiryDocument>(
                Builders<ClaimStatusInquiryDocument>.IndexKeys.Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ClaimStatusInquiryDocument>(
                Builders<ClaimStatusInquiryDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.GatewayName)
                    .Ascending(d => d.ExternalTransactionId)),
            new CreateIndexModel<ClaimStatusInquiryDocument>(
                Builders<ClaimStatusInquiryDocument>.IndexKeys.Ascending(d => d.TransmissionId)),
            new CreateIndexModel<ClaimStatusInquiryDocument>(
                Builders<ClaimStatusInquiryDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.ClaimId))
        }, ct).ConfigureAwait(false);

        await _remittances.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys.Ascending(d => d.Id),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys.Ascending(d => d.IdempotencyKey),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys.Ascending(d => d.EventKey),
                new CreateIndexOptions { Unique = true, Sparse = true }),
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys.Ascending(d => d.HasPendingOutbox)),
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys.Ascending(d => d.TenantId)),
            new CreateIndexModel<RemittanceReceiptDocument>(
                Builders<RemittanceReceiptDocument>.IndexKeys
                    .Ascending(d => d.TenantId)
                    .Ascending(d => d.Status))
        }, ct).ConfigureAwait(false);
    }

    async Task<ClaimTransmissionRecord?> IClaimTransmissionStore.GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct)
    {
        var doc = await _transmissions
            .Find(d => d.TenantId == tenantId && d.IdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    async Task<ClaimTransmissionRecord?> IClaimTransmissionStore.GetByIdAsync(
        string transmissionId, CancellationToken ct)
    {
        var doc = await _transmissions.Find(d => d.Id == transmissionId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(
        string gatewayName, string submissionId, CancellationToken ct = default) =>
        FindTransmissionsAsync(d => d.GatewayName == gatewayName && d.SubmissionId == submissionId, ct);

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(
        string gatewayName, string externalTransactionId, CancellationToken ct = default) =>
        FindTransmissionsAsync(
            d => d.GatewayName == gatewayName && d.ExternalTransactionId == externalTransactionId, ct);

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(
        string gatewayName, string patientControlNumber, CancellationToken ct = default) =>
        FindTransmissionsAsync(
            d => d.GatewayName == gatewayName && d.PatientControlNumber == patientControlNumber, ct);

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPayerClaimControlNumberAsync(
        string gatewayName, string payerClaimControlNumber, CancellationToken ct = default) =>
        FindTransmissionsAsync(
            d => d.GatewayName == gatewayName && d.Payload.PayerClaimControlNumber == payerClaimControlNumber, ct);

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(
        string gatewayName, string correlationId, CancellationToken ct = default) =>
        FindTransmissionsAsync(
            d => d.GatewayName == gatewayName && d.CorrelationId == correlationId, ct);

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByTenantAndClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default) =>
        FindTransmissionsAsync(d => d.TenantId == tenantId && d.Payload.ClaimId == claimId, ct);

    public Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default) =>
        _transmissions.ReplaceOneAsync(
            d => d.Id == record.TransmissionId,
            ClaimTransmissionDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(
        ClaimTransmissionRecord record, CancellationToken ct = default)
    {
        try
        {
            await _transmissions
                .InsertOneAsync(ClaimTransmissionDocument.FromModel(record), cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await ((IClaimTransmissionStore)this)
                .GetByIdempotencyKeyAsync(record.TenantId, record.IdempotencyKey, ct)
                .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    async Task<ClaimAcknowledgmentRecord?> IClaimAcknowledgmentStore.GetByIdempotencyKeyAsync(
        string gateway, string acknowledgmentId, CancellationToken ct)
    {
        var key = $"{gateway}|{acknowledgmentId}";
        var doc = await _acknowledgments.Find(d => d.IdempotencyKey == key)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    public async Task<ClaimAcknowledgmentRecord?> GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        var key = $"{gateway}|{eventId}";
        var doc = await _acknowledgments.Find(d => d.EventKey == key)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    async Task<ClaimAcknowledgmentRecord?> IClaimAcknowledgmentStore.GetByIdAsync(
        string recordId, CancellationToken ct)
    {
        var doc = await _acknowledgments.Find(d => d.Id == recordId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    public async Task<IReadOnlyList<ClaimAcknowledgmentRecord>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default)
    {
        var docs = await _acknowledgments
            .Find(d => d.Payload.TransmissionId == transmissionId)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    public Task SaveAsync(ClaimAcknowledgmentRecord record, CancellationToken ct = default) =>
        _acknowledgments.ReplaceOneAsync(
            d => d.Id == record.RecordId,
            ClaimAcknowledgmentDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, ClaimAcknowledgmentRecord Record)> TryCreateAsync(
        ClaimAcknowledgmentRecord record, CancellationToken ct = default)
    {
        var doc = ClaimAcknowledgmentDocument.FromModel(record);
        try
        {
            await _acknowledgments.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await ((IClaimAcknowledgmentStore)this)
                .GetByIdempotencyKeyAsync(record.Gateway, record.AcknowledgmentId, ct)
                .ConfigureAwait(false)
                ?? await GetByEventIdAsync(record.Gateway, record.EventId ?? string.Empty, ct)
                    .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    public async Task<IReadOnlyList<ClaimAcknowledgmentRecord>> ListPendingOutboxAsync(
        int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _acknowledgments
            .Find(d => d.HasPendingOutbox)
            .SortBy(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    public async Task<IReadOnlyList<ClaimAcknowledgmentRecord>> ListByStatusAsync(
        ClaimAcknowledgmentStatus status, int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _acknowledgments
            .Find(d => d.Payload.Status == status)
            .SortByDescending(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    public async Task<ClaimAcknowledgmentCursor?> GetAsync(string gatewayName, CancellationToken ct = default) =>
        await _cursors.Find(c => c.GatewayName == gatewayName).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public Task SaveAsync(ClaimAcknowledgmentCursor cursor, CancellationToken ct = default) =>
        _cursors.ReplaceOneAsync(
            c => c.GatewayName == cursor.GatewayName,
            cursor,
            new ReplaceOptions { IsUpsert = true },
            ct);

    async Task<ClaimAttachmentTransmissionRecord?> IClaimAttachmentTransmissionStore.GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct)
    {
        var doc = await _attachments
            .Find(d => d.TenantId == tenantId && d.IdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    async Task<ClaimAttachmentTransmissionRecord?> IClaimAttachmentTransmissionStore.GetByIdAsync(
        string attachmentTransmissionId, CancellationToken ct)
    {
        var doc = await _attachments.Find(d => d.Id == attachmentTransmissionId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> ListByClaimTransmissionIdAsync(
        string claimTransmissionId, CancellationToken ct = default)
    {
        var docs = await _attachments
            .Find(d => d.ClaimTransmissionId == claimTransmissionId)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> FindByChecksumAsync(
        string tenantId, string checksumSha256, CancellationToken ct = default)
    {
        var docs = await _attachments
            .Find(d => d.TenantId == tenantId && d.ChecksumSha256 == checksumSha256)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public Task SaveAsync(ClaimAttachmentTransmissionRecord record, CancellationToken ct = default) =>
        _attachments.ReplaceOneAsync(
            d => d.Id == record.AttachmentTransmissionId,
            ClaimAttachmentTransmissionDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, ClaimAttachmentTransmissionRecord Record)> TryCreateAsync(
        ClaimAttachmentTransmissionRecord record, CancellationToken ct = default)
    {
        try
        {
            await _attachments
                .InsertOneAsync(ClaimAttachmentTransmissionDocument.FromModel(record), cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await ((IClaimAttachmentTransmissionStore)this)
                .GetByIdempotencyKeyAsync(record.TenantId, record.IdempotencyKey, ct)
                .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    async Task<InboundClaimAttachmentReceipt?> IInboundClaimAttachmentReceiptStore.GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct)
    {
        var doc = await _inboundAttachments.Find(d => d.IdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    async Task<InboundClaimAttachmentReceipt?> IInboundClaimAttachmentReceiptStore.GetByIdAsync(
        string receiptId, CancellationToken ct)
    {
        var doc = await _inboundAttachments.Find(d => d.Id == receiptId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    public async Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        var docs = await _inboundAttachments
            .Find(d => d.TenantId == tenantId && d.ClaimId == claimId)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    async Task<IReadOnlyList<InboundClaimAttachmentReceipt>> IInboundClaimAttachmentReceiptStore.ListPendingOutboxAsync(
        int take, CancellationToken ct)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _inboundAttachments
            .Find(d => d.HasPendingOutbox)
            .SortBy(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public Task SaveAsync(InboundClaimAttachmentReceipt record, CancellationToken ct = default) =>
        _inboundAttachments.ReplaceOneAsync(
            d => d.Id == record.ReceiptId,
            InboundClaimAttachmentReceiptDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, InboundClaimAttachmentReceipt Record)> TryCreateAsync(
        InboundClaimAttachmentReceipt record, CancellationToken ct = default)
    {
        try
        {
            await _inboundAttachments
                .InsertOneAsync(InboundClaimAttachmentReceiptDocument.FromModel(record), cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await ((IInboundClaimAttachmentReceiptStore)this)
                .GetByIdempotencyKeyAsync(record.IdempotencyKey, ct)
                .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    async Task<ClaimStatusInquiryRecord?> IClaimStatusInquiryStore.GetByIdAsync(
        string inquiryId, CancellationToken ct)
    {
        var doc = await _statusInquiries.Find(d => d.Id == inquiryId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    public async Task<ClaimStatusInquiryRecord?> GetByExternalTransactionIdAsync(
        string tenantId, string gatewayName, string externalTransactionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalTransactionId))
        {
            return null;
        }

        var doc = await _statusInquiries
            .Find(d => d.TenantId == tenantId &&
                       d.GatewayName == gatewayName &&
                       d.ExternalTransactionId == externalTransactionId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.ToModel();
    }

    async Task<IReadOnlyList<ClaimStatusInquiryRecord>> IClaimStatusInquiryStore.ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct)
    {
        var docs = await _statusInquiries
            .Find(d => d.TransmissionId == transmissionId)
            .SortBy(d => d.Payload.RequestedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ClaimStatusInquiryRecord>> ListByTenantAndClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        var docs = await _statusInquiries
            .Find(d => d.TenantId == tenantId && d.ClaimId == claimId)
            .SortBy(d => d.Payload.RequestedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }

    public Task SaveAsync(ClaimStatusInquiryRecord record, CancellationToken ct = default) =>
        _statusInquiries.ReplaceOneAsync(
            d => d.Id == record.InquiryId,
            ClaimStatusInquiryDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, ClaimStatusInquiryRecord Record)> TryCreateAsync(
        ClaimStatusInquiryRecord record, CancellationToken ct = default)
    {
        try
        {
            await _statusInquiries
                .InsertOneAsync(ClaimStatusInquiryDocument.FromModel(record), cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = string.IsNullOrWhiteSpace(record.ExternalTransactionId)
                ? null
                : await GetByExternalTransactionIdAsync(
                    record.TenantId, record.GatewayName, record.ExternalTransactionId, ct)
                    .ConfigureAwait(false);
            existing ??= await ((IClaimStatusInquiryStore)this).GetByIdAsync(record.InquiryId, ct)
                .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    async Task<RemittanceReceipt?> IRemittanceStore.GetByIdempotencyKeyAsync(
        string gateway, string remittanceId, CancellationToken ct)
    {
        var key = $"{gateway}|{remittanceId}";
        var doc = await _remittances.Find(d => d.IdempotencyKey == key)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    async Task<RemittanceReceipt?> IRemittanceStore.GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        var key = $"{gateway}|{eventId}";
        var doc = await _remittances.Find(d => d.EventKey == key)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    async Task<RemittanceReceipt?> IRemittanceStore.GetByIdAsync(string receiptId, CancellationToken ct)
    {
        var doc = await _remittances.Find(d => d.Id == receiptId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return doc?.Payload;
    }

    async Task<IReadOnlyList<RemittanceReceipt>> IRemittanceStore.ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct)
    {
        var docs = await _remittances.Find(d => d.TransmissionIds.Contains(transmissionId))
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    public async Task<IReadOnlyList<RemittanceReceipt>> ListByTenantAsync(
        string tenantId, int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _remittances
            .Find(d => d.TenantId == tenantId)
            .SortByDescending(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    public Task SaveAsync(RemittanceReceipt record, CancellationToken ct = default) =>
        _remittances.ReplaceOneAsync(
            d => d.Id == record.ReceiptId,
            RemittanceReceiptDocument.FromModel(record),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<(bool Created, RemittanceReceipt Record)> TryCreateAsync(
        RemittanceReceipt record, CancellationToken ct = default)
    {
        try
        {
            await _remittances
                .InsertOneAsync(RemittanceReceiptDocument.FromModel(record), cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, record);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await ((IRemittanceStore)this)
                .GetByIdempotencyKeyAsync(record.Gateway, record.RemittanceId, ct)
                .ConfigureAwait(false)
                ?? await ((IRemittanceStore)this)
                    .GetByEventIdAsync(record.Gateway, record.EventId ?? string.Empty, ct)
                    .ConfigureAwait(false);
            return (false, existing ?? record);
        }
    }

    async Task<IReadOnlyList<RemittanceReceipt>> IRemittanceStore.ListPendingOutboxAsync(
        int take, CancellationToken ct)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _remittances
            .Find(d => d.HasPendingOutbox)
            .SortBy(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    async Task<IReadOnlyList<RemittanceReceipt>> IRemittanceStore.ListAvailableForPostingAsync(
        string tenantId, int take, CancellationToken ct)
    {
        var limit = take <= 0 ? 50 : take;
        var docs = await _remittances
            .Find(d => d.TenantId == tenantId && d.Status == RemittanceLifecycleStatus.AvailableForPosting)
            .SortBy(d => d.Payload.ReceivedAtUtc)
            .Limit(limit)
            .ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.Payload).ToList();
    }

    private async Task<IReadOnlyList<ClaimTransmissionRecord>> FindTransmissionsAsync(
        System.Linq.Expressions.Expression<Func<ClaimTransmissionDocument, bool>> filter,
        CancellationToken ct)
    {
        var docs = await _transmissions.Find(filter).ToListAsync(ct).ConfigureAwait(false);
        return docs.Select(d => d.ToModel()).ToList();
    }
}

internal sealed class ClaimTransmissionDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string GatewayName { get; set; } = string.Empty;

    public string? SubmissionId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? CorrelationId { get; set; }

    public ClaimTransmissionRecord Payload { get; set; } = new();

    public static ClaimTransmissionDocument FromModel(ClaimTransmissionRecord r) => new()
    {
        Id = r.TransmissionId,
        TenantId = r.TenantId,
        IdempotencyKey = r.IdempotencyKey,
        GatewayName = r.GatewayName,
        SubmissionId = r.SubmissionId,
        ExternalTransactionId = r.ExternalTransactionId,
        PatientControlNumber = r.PatientControlNumber,
        CorrelationId = r.CorrelationId,
        Payload = r
    };

    public ClaimTransmissionRecord ToModel() => Payload;
}

internal sealed class ClaimAcknowledgmentDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? EventKey { get; set; }

    public bool HasPendingOutbox { get; set; }

    public ClaimAcknowledgmentRecord Payload { get; set; } = new();

    public static ClaimAcknowledgmentDocument FromModel(ClaimAcknowledgmentRecord r) => new()
    {
        Id = r.RecordId,
        IdempotencyKey = r.IdempotencyKey,
        EventKey = string.IsNullOrWhiteSpace(r.EventId) ? null : $"{r.Gateway}|{r.EventId}",
        HasPendingOutbox = r.HasPendingOutbox,
        Payload = r
    };
}

internal sealed class ClaimAttachmentTransmissionDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string ClaimTransmissionId { get; set; } = string.Empty;

    public string? ChecksumSha256 { get; set; }

    public ClaimAttachmentTransmissionRecord Payload { get; set; } = new();

    public static ClaimAttachmentTransmissionDocument FromModel(ClaimAttachmentTransmissionRecord r) => new()
    {
        Id = r.AttachmentTransmissionId,
        TenantId = r.TenantId,
        IdempotencyKey = r.IdempotencyKey,
        ClaimTransmissionId = r.ClaimTransmissionId,
        ChecksumSha256 = r.ChecksumSha256,
        Payload = r
    };

    public ClaimAttachmentTransmissionRecord ToModel() => Payload;
}

internal sealed class InboundClaimAttachmentReceiptDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public bool HasPendingOutbox { get; set; }

    public InboundClaimAttachmentReceipt Payload { get; set; } = new();

    public static InboundClaimAttachmentReceiptDocument FromModel(InboundClaimAttachmentReceipt r) => new()
    {
        Id = r.ReceiptId,
        IdempotencyKey = r.IdempotencyKey,
        TenantId = r.TenantId,
        ClaimId = r.ClaimId,
        HasPendingOutbox = r.HasPendingOutbox,
        Payload = r
    };

    public InboundClaimAttachmentReceipt ToModel() => Payload;
}

internal sealed class ClaimStatusInquiryDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string? TransmissionId { get; set; }

    public string GatewayName { get; set; } = string.Empty;

    public string? ExternalTransactionId { get; set; }

    public ClaimStatusInquiryRecord Payload { get; set; } = new();

    public static ClaimStatusInquiryDocument FromModel(ClaimStatusInquiryRecord r) => new()
    {
        Id = r.InquiryId,
        IdempotencyKey = r.IdempotencyKey,
        TenantId = r.TenantId,
        ClaimId = r.ClaimId,
        TransmissionId = r.TransmissionId,
        GatewayName = r.GatewayName,
        ExternalTransactionId = r.ExternalTransactionId,
        Payload = r
    };

    public ClaimStatusInquiryRecord ToModel() => Payload;
}

internal sealed class RemittanceReceiptDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? EventKey { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public RemittanceLifecycleStatus Status { get; set; }

    public bool HasPendingOutbox { get; set; }

    public List<string> TransmissionIds { get; set; } = new();

    public RemittanceReceipt Payload { get; set; } = new();

    public static RemittanceReceiptDocument FromModel(RemittanceReceipt r) => new()
    {
        Id = r.ReceiptId,
        IdempotencyKey = r.IdempotencyKey,
        EventKey = string.IsNullOrWhiteSpace(r.EventId) ? null : $"{r.Gateway}|{r.EventId}",
        TenantId = r.TenantId,
        Status = r.Status,
        HasPendingOutbox = r.HasPendingOutbox,
        TransmissionIds = r.Claims
            .Select(c => c.TransmissionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList(),
        Payload = r
    };
}
