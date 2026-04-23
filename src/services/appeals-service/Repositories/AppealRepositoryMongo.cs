using AppealsService.Models;
using MongoDB.Driver;

namespace AppealsService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IAppealRepository"/>. The
/// transition-and-append shape is not truly atomic across the Appeals and
/// AppealEvents collections — Mongo multi-collection transactions require
/// a replica-set primary, which our dev and many production deployments do
/// not guarantee. Operationally we accept that a transition-then-event
/// crash window can drop a single audit row; the appeal record update is
/// still conditional (filter on current Status) so the status invariant
/// holds. If operations observe a gap, the source of truth is the appeal
/// row itself — the event log is an audit annotation, not the authoritative
/// lifecycle store. Same inherited posture as consent-service and
/// personal-representative-service.
/// </summary>
public sealed class AppealRepositoryMongo : IAppealRepository
{
    public const string AppealsCollectionName = "Appeals";

    private readonly IMongoCollection<Appeal> _appeals;
    private readonly IAppealEventSink _events;

    public AppealRepositoryMongo(IMongoDatabase database, IAppealEventSink events)
    {
        _appeals = database.GetCollection<Appeal>(AppealsCollectionName);
        _events = events;
    }

    /// <summary>
    /// Internal test seam for the migration hosted service — exposes the
    /// raw collection so a one-shot batch scan can rewrite legacy-status
    /// records. Not part of <see cref="IAppealRepository"/>.
    /// </summary>
    internal IMongoCollection<Appeal> AppealsCollection => _appeals;

    public async Task<Appeal> CreateAsync(Appeal appeal, AppealEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appeal.Id)) appeal.Id = Guid.NewGuid().ToString();
        if (appeal.CreatedAt == default) appeal.CreatedAt = DateTime.UtcNow;
        await _appeals.InsertOneAsync(appeal, cancellationToken: ct);
        await _events.AppendAsync(genesisEvent, ct);
        return appeal;
    }

    public async Task<Appeal?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, id);
        return await _appeals.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Appeal?> GetByAppealNumberAsync(string tenantId, string appealNumber, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.AppealNumber, appealNumber);
        return await _appeals.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Appeal>> GetByClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.ClaimId, claimId);
        return await _appeals.Find(filter)
            .SortByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Appeal?> GetMostRecentAppealByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.ClaimId, claimId)
                   & Builders<Appeal>.Filter.Ne(a => a.Status, AppealStatus.Closed);
        return await _appeals.Find(filter)
            .SortByDescending(a => a.SubmittedDate)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Appeal>> SearchAsync(
        string tenantId, AppealSearchParams p, CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<Appeal>>
        {
            Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(p.MemberId))
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.MemberId, p.MemberId));
        if (!string.IsNullOrEmpty(p.ProviderNPI))
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.ProviderNPI, p.ProviderNPI));
        if (p.SubmittedFrom.HasValue)
            filters.Add(Builders<Appeal>.Filter.Gte(a => a.SubmittedDate, p.SubmittedFrom.Value));
        if (p.SubmittedTo.HasValue)
            filters.Add(Builders<Appeal>.Filter.Lte(a => a.SubmittedDate, p.SubmittedTo.Value));
        if (p.Status.HasValue)
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.Status, p.Status.Value));
        if (p.ClosureReasonCode.HasValue)
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.ClosureReasonCode, p.ClosureReasonCode.Value));
        if (p.LineOfBusiness.HasValue)
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.LineOfBusiness, p.LineOfBusiness.Value));
        if (!string.IsNullOrEmpty(p.AssignedReviewerId))
            filters.Add(Builders<Appeal>.Filter.Eq(a => a.AssignedReviewerId, p.AssignedReviewerId));

        var page = Math.Max(1, p.Page);
        var pageSize = Math.Clamp(p.PageSize, 1, 100);

        return await _appeals
            .Find(Builders<Appeal>.Filter.And(filters))
            .SortByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
    }

    public async Task<AppealsSummary> GetAppealsSummaryAsync(
        string tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // TODO(appeals-followup-summary-perf): in-memory aggregation over all
        // rows scans poorly at scale. A follow-up PR will migrate this to a
        // Mongo aggregation pipeline / Cosmos GROUP BY. Correctness is fine;
        // only performance is deferred.
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Gte(a => a.SubmittedDate, from)
                   & Builders<Appeal>.Filter.Lte(a => a.SubmittedDate, to);
        var appeals = await _appeals.Find(filter).ToListAsync(ct);
        return SummaryBuilder.Build(appeals);
    }

    public async Task<Appeal> TransitionStatusAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id)
                   & Builders<Appeal>.Filter.Eq(a => a.Status, expectedFromStatus);

        appeal.UpdatedAt = DateTime.UtcNow;
        var replaceResult = await _appeals.ReplaceOneAsync(filter, appeal, cancellationToken: ct);
        if (replaceResult.MatchedCount == 0)
        {
            throw new InvalidAppealTransitionException(expectedFromStatus, appeal.Status);
        }

        await _events.AppendAsync(auditEvent, ct);
        return appeal;
    }

    public async Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var nonTerminalStatuses = new[] { AppealStatus.Submitted, AppealStatus.InReview, AppealStatus.PendingInfo };

        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id)
                   & Builders<Appeal>.Filter.Eq(a => a.OverdueAuditEmitted, false)
                   & Builders<Appeal>.Filter.In(a => a.Status, nonTerminalStatuses);

        var update = Builders<Appeal>.Update
            .Set(a => a.OverdueAuditEmitted, true)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };

        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct);
        if (updated is null) return null;

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal> AppendNoteAsync(Appeal appeal, AppealNote note, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        var update = Builders<Appeal>.Update
            .Push(a => a.Notes, note)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };

        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct)
            ?? throw new InvalidOperationException(
                $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal> AppendAttachmentAsync(Appeal appeal, AppealAttachment attachment, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        var updateBuilder = Builders<Appeal>.Update
            .Push(a => a.Attachments, attachment)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(attachment.ControlNumber))
        {
            updateBuilder = updateBuilder.Push(a => a.AttachmentControlNumbers, attachment.ControlNumber);
        }

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };

        var updated = await _appeals.FindOneAndUpdateAsync(filter, updateBuilder, options, ct)
            ?? throw new InvalidOperationException(
                $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal> AcknowledgeAttachmentAsync(
        string tenantId, string appealId, string attachmentId, bool acknowledgmentReceived,
        AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appealId)
                   & Builders<Appeal>.Filter.ElemMatch(
                         a => a.Attachments,
                         att => att.AttachmentId == attachmentId);

        var newStatus = acknowledgmentReceived ? AttachmentStatus.Acknowledged : AttachmentStatus.Sent;
        var update = Builders<Appeal>.Update
            .Set("attachments.$.acknowledgmentReceived", acknowledgmentReceived)
            .Set("attachments.$.status", newStatus)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        if (acknowledgmentReceived)
        {
            update = update.Set("attachments.$.sentDate", DateTime.UtcNow);
        }

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };
        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct)
            ?? throw new InvalidOperationException(
                $"Appeal {appealId} with attachment {attachmentId} not found for tenant {tenantId}.");

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal> AssignReviewerAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        var update = Builders<Appeal>.Update
            .Set(a => a.AssignedReviewerId, appeal.AssignedReviewerId)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };
        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct)
            ?? throw new InvalidOperationException(
                $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<AppealNoteLookup?> GetNoteByIdAsync(string tenantId, string noteId, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.ElemMatch(a => a.Notes, n => n.NoteId == noteId);

        var appeal = await _appeals.Find(filter).FirstOrDefaultAsync(ct);
        if (appeal is null) return null;

        var note = appeal.Notes.FirstOrDefault(n => n.NoteId == noteId);
        if (note is null) return null;

        return new AppealNoteLookup
        {
            AppealId = appeal.Id,
            MemberId = appeal.MemberId,
            NoteId = note.NoteId,
            CreatedBy = note.CreatedBy,
            NoteText = note.NoteText,
            IsInternal = note.IsInternal,
            CreatedAt = note.CreatedAt
        };
    }

    public async Task<AppealAttachmentLookup?> GetAttachmentByIdAsync(string tenantId, string attachmentId, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<Appeal>.Filter.ElemMatch(a => a.Attachments, a => a.AttachmentId == attachmentId);

        var appeal = await _appeals.Find(filter).FirstOrDefaultAsync(ct);
        if (appeal is null) return null;

        var att = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
        if (att is null) return null;

        return new AppealAttachmentLookup
        {
            AppealId = appeal.Id,
            MemberId = appeal.MemberId,
            AttachmentId = att.AttachmentId,
            ControlNumber = att.ControlNumber,
            AttachmentTypeCode = att.AttachmentTypeCode,
            AttachmentTypeDescription = att.AttachmentTypeDescription,
            TransmissionCode = att.TransmissionCode,
            FileName = att.FileName,
            BlobUrl = att.BlobUrl,
            ContentType = att.ContentType,
            FileSizeBytes = att.FileSizeBytes,
            UploadedAt = att.UploadedAt,
            Description = att.Description,
            Status = att.Status,
            SentDate = att.SentDate,
            AcknowledgmentReceived = att.AcknowledgmentReceived
        };
    }
}
