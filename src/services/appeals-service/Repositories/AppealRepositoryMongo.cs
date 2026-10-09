using MongoDB.Bson;
using AppealsService.Models;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

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
///
/// The Kafka event is different: it is pushed onto the appeal's own
/// <see cref="Appeal.Outbox"/> array in the SAME single-document update as
/// the change (<see cref="WithOutbox"/>). Single-document writes are atomic
/// on a standalone mongod, so no replica set is needed and a crash can no
/// longer separate a state change from its event. This class is also the
/// <see cref="IAppealOutboxStore"/> the dispatcher drains.
/// </summary>
public sealed class AppealRepositoryMongo : IAppealRepository, IAppealOutboxStore
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
        (appeal.Outbox ??= new()).Add(AppealsService.Services.AppealOutbox.Require(genesisEvent));
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

        // Targeted update, not a replace: only the transition-owned fields
        // are written, so a snapshot read before a concurrent attachment,
        // acknowledgment, note, reviewer assignment, extension or overdue
        // observation cannot overwrite it. The status filter refuses a
        // concurrent transition.
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id)
                   & Builders<Appeal>.Filter.Eq(a => a.Status, expectedFromStatus);

        appeal.UpdatedAt = DateTime.UtcNow;
        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };
        var updated = await _appeals.FindOneAndUpdateAsync(
                filter, WithOutbox(AppealStatusTransitionFields.ToMongoUpdate(appeal), auditEvent), options, ct)
            ?? throw new InvalidAppealTransitionException(expectedFromStatus, appeal.Status);

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var nonTerminalStatuses = new[] { AppealStatus.Submitted, AppealStatus.InReview, AppealStatus.PendingInfo };

        // Also pinned to the caller's status: the event payload (built
        // before the write) reports it as currentStatus.
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id)
                   & Builders<Appeal>.Filter.Eq(a => a.OverdueAuditEmitted, false)
                   & Builders<Appeal>.Filter.In(a => a.Status, nonTerminalStatuses)
                   & Builders<Appeal>.Filter.Eq(a => a.Status, appeal.Status);

        var update = WithOutbox(Builders<Appeal>.Update
            .Set(a => a.OverdueAuditEmitted, true)
            .Set(a => a.UpdatedAt, DateTime.UtcNow), auditEvent);

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };

        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct);
        if (updated is null) return null;

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    public async Task<Appeal?> TryExtendDeadlineAsync(
        Appeal appeal, AppealNote? justificationNote,
        Func<Appeal, IReadOnlyList<AppealEvent>> buildAuditEvents,
        CancellationToken ct = default)
    {
        var nonTerminalStatuses = new[] { AppealStatus.Submitted, AppealStatus.InReview, AppealStatus.PendingInfo };
        var idFilter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                     & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        // Filter on DeadlineExtension == null makes the extension one-shot
        // under concurrency: a second writer's filter no longer matches.
        // The justification note rides in the same single-document update.
        var filter = idFilter
                   & Builders<Appeal>.Filter.Eq(a => a.DeadlineExtension, null)
                   & Builders<Appeal>.Filter.In(a => a.Status, nonTerminalStatuses);

        var update = Builders<Appeal>.Update
            .Set(a => a.TargetResponseDate, appeal.TargetResponseDate)
            .Set(a => a.DeadlineExtension, appeal.DeadlineExtension)
            .Set(a => a.UpdatedAt, appeal.UpdatedAt ?? DateTime.UtcNow)
            .Set(a => a.UpdatedBy, appeal.UpdatedBy);
        if (justificationNote is not null) update = update.Push(a => a.Notes, justificationNote);
        // The events are built from the proposed extension, which is the
        // persisted one whenever this write wins; a replay or a lost race
        // writes nothing here, and the winner's own outbox entries publish.
        var outbox = AppealsService.Services.AppealOutbox.MessagesOf(buildAuditEvents(appeal));
        if (outbox.Count > 0) update = update.PushEach(a => a.Outbox, outbox);

        var options = new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After };
        var updated = await _appeals.FindOneAndUpdateAsync(filter, update, options, ct);

        if (updated is null)
        {
            // Not written: either a different extension / closed appeal, or
            // a replay of this very extension whose follow-up steps failed.
            var persisted = await _appeals.Find(idFilter).FirstOrDefaultAsync(ct);
            if (persisted is null || !AppealRepository.IsSameExtension(persisted, appeal)) return null;
            updated = persisted;
        }

        foreach (var evt in buildAuditEvents(updated)) await _events.AppendAsync(evt, ct);
        return updated;
    }

    public async Task<Appeal> AppendNoteAsync(Appeal appeal, AppealNote note, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        var update = Builders<Appeal>.Update
            .Push(a => a.Notes, note)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        return await ApplyOnceAsync(filter, update, auditEvent,
            $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.", ct);
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

        return await ApplyOnceAsync(filter, updateBuilder, auditEvent,
            $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.", ct);
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

        // Typed positional paths: the Appeal class map keeps .NET (Pascal)
        // element names, so string paths like "attachments.$.status" never
        // matched the stored array and the acknowledgment was not persisted.
        var newStatus = acknowledgmentReceived ? AttachmentStatus.Acknowledged : AttachmentStatus.Sent;
        var update = Builders<Appeal>.Update
            .Set(a => a.Attachments.FirstMatchingElement().AcknowledgmentReceived, acknowledgmentReceived)
            .Set(a => a.Attachments.FirstMatchingElement().Status, newStatus)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        if (acknowledgmentReceived)
        {
            // The audit row's time, so the event's sentDate matches what is stored.
            update = update.Set(a => a.Attachments.FirstMatchingElement().SentDate, auditEvent.OccurredAt);
        }
        return await ApplyOnceAsync(filter, update, auditEvent,
            $"Appeal {appealId} with attachment {attachmentId} not found for tenant {tenantId}.", ct);
    }

    public async Task<Appeal> AssignReviewerAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(a => a.TenantId, appeal.TenantId)
                   & Builders<Appeal>.Filter.Eq(a => a.Id, appeal.Id);

        var update = Builders<Appeal>.Update
            .Set(a => a.AssignedReviewerId, appeal.AssignedReviewerId)
            .Set(a => a.UpdatedAt, DateTime.UtcNow);

        return await ApplyOnceAsync(filter, update, auditEvent,
            $"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.", ct);
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

    /// <summary>Adds the event's outbox entry to the same update as the change; a change without one is refused.</summary>
    internal static UpdateDefinition<Appeal> WithOutbox(UpdateDefinition<Appeal> update, AppealEvent auditEvent) =>
        update.Push(a => a.Outbox, AppealsService.Services.AppealOutbox.Require(auditEvent));

    /// <summary>
    /// Idempotent append-style change: the update (with its outbox entry) is
    /// applied only while the appeal's outbox does not already hold the
    /// event's idempotency key (<c>Outbox.IdempotencyKey $ne key</c> in the
    /// same filter). A retry with the same key is a replay: nothing is
    /// appended and the persisted appeal is returned. The audit append is
    /// idempotent on EventId either way.
    /// </summary>
    private async Task<Appeal> ApplyOnceAsync(
        FilterDefinition<Appeal> filter, UpdateDefinition<Appeal> update, AppealEvent auditEvent,
        string notFound, CancellationToken ct)
    {
        var key = AppealsService.Services.AppealOutbox.Require(auditEvent).IdempotencyKey;
        var keyPath = $"{nameof(Appeal.Outbox)}.{nameof(AppealOutboxMessage.IdempotencyKey)}";
        var guarded = filter & Builders<Appeal>.Filter.Ne(keyPath, key);
        var updated = await _appeals.FindOneAndUpdateAsync(guarded, WithOutbox(update, auditEvent),
            new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After }, ct);
        if (updated is null)
        {
            updated = await _appeals.Find(ById(auditEvent.TenantId, auditEvent.AppealId)
                                          & Builders<Appeal>.Filter.Eq(keyPath, key))
                          .FirstOrDefaultAsync(ct)
                      ?? throw new InvalidOperationException(notFound);
        }

        await _events.AppendAsync(auditEvent, ct);
        return updated;
    }

    // ── IAppealOutboxStore ──────────────────────────────────────────────
    // Element paths are the class-map (PascalCase) names; enum values are
    // stored as strings (Program.cs convention), so raw paths compare to
    // the enum names.

    private const string StatusPath = "Outbox.Status";

    private static FilterDefinition<Appeal> ById(string tenantId, string appealId) =>
        Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId)
        & Builders<Appeal>.Filter.Eq(a => a.Id, appealId);

    private static FilterDefinition<Appeal> LeaseFree(DateTime now) =>
        Builders<Appeal>.Filter.Eq(a => a.OutboxLeaseUntil, null)
        | Builders<Appeal>.Filter.Lt(a => a.OutboxLeaseUntil, now);

    private static FilterDefinition<Appeal> PendingDue(DateTime now) =>
        // Top-level equality first: it is what lets the planner use the
        // partial index ix_outbox_pending (an $elemMatch alone does not).
        Builders<Appeal>.Filter.Eq(StatusPath, nameof(AppealOutboxStatus.Pending))
        & Builders<Appeal>.Filter.ElemMatch(a => a.Outbox,
            m => m.Status == AppealOutboxStatus.Pending && (m.NextAttemptAt == null || m.NextAttemptAt <= now));

    private static FilterDefinition<Appeal> DeadLetterExpired(DateTime now) =>
        Builders<Appeal>.Filter.Eq(StatusPath, nameof(AppealOutboxStatus.DeadLettered))
        & Builders<Appeal>.Filter.ElemMatch(a => a.Outbox,
            m => m.Status == AppealOutboxStatus.DeadLettered && m.ExpiresAt != null && m.ExpiresAt <= now);

    /// <summary>The sweep's pending-entry filter (exposed for the query-plan test).</summary>
    internal static FilterDefinition<Appeal> DueFilter(DateTime now) => PendingDue(now) & LeaseFree(now);

    public async Task<IReadOnlyList<AppealOutboxKey>> FindDueAsync(DateTime now, int limit, CancellationToken ct = default)
    {
        var keys = new List<AppealOutboxKey>();
        foreach (var filter in new[] { DueFilter(now), DeadLetterExpired(now) & LeaseFree(now) })
        {
            if (keys.Count >= limit) break;
            var rows = await _appeals.Find(filter)
                .Project(a => new { a.TenantId, a.Id })
                .Limit(limit - keys.Count)
                .ToListAsync(ct);
            keys.AddRange(rows.Select(r => new AppealOutboxKey(r.TenantId, r.Id)).Where(k => !keys.Contains(k)));
        }
        return keys;
    }

    public async Task<AppealOutboxLease?> TryLeaseAsync(
        string tenantId, string appealId, string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default)
    {
        var filter = ById(tenantId, appealId)
                   & (LeaseFree(now) | Builders<Appeal>.Filter.Eq(a => a.OutboxLeaseOwner, owner))
                   & (PendingDue(now) | DeadLetterExpired(now));
        var update = Builders<Appeal>.Update
            .Set(a => a.OutboxLeaseOwner, owner)
            .Set(a => a.OutboxLeaseUntil, leaseUntil);
        var leased = await _appeals.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<Appeal> { ReturnDocument = ReturnDocument.After }, ct);
        return leased is null
            ? null
            : new AppealOutboxLease(leased.Outbox ?? new List<AppealOutboxMessage>(), leased.OutboxSequence ?? 0);
    }

    public async Task<bool> RenewLeaseAsync(
        string tenantId, string appealId, string owner, DateTime leaseUntil,
        AppealOutboxSequenceAssignment? assign, CancellationToken ct = default)
    {
        var filter = ById(tenantId, appealId) & Builders<Appeal>.Filter.Eq(a => a.OutboxLeaseOwner, owner);
        var update = Builders<Appeal>.Update.Set(a => a.OutboxLeaseUntil, leaseUntil);
        if (assign is not null)
        {
            filter &= assign.Sequence == 1
                ? Builders<Appeal>.Filter.Eq(a => a.OutboxSequence, null)
                : Builders<Appeal>.Filter.Eq(a => a.OutboxSequence, assign.Sequence - 1);
            filter &= Builders<Appeal>.Filter.ElemMatch(a => a.Outbox,
                m => m.Id == assign.EntryId && m.Sequence == null);
            update = update
                .Set(a => a.OutboxSequence, assign.Sequence)
                .Set(a => a.Outbox!.FirstMatchingElement().Sequence, assign.Sequence);
        }
        var result = await _appeals.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<bool> UpdateMessageAsync(
        string tenantId, string appealId, string owner, AppealOutboxMessage message, CancellationToken ct = default)
    {
        // Positional update on THIS row (server id) while it is still
        // pending and our lease is live: never another row with the same
        // event id, never a result a newer lease holder already wrote.
        var filter = ById(tenantId, appealId)
                   & Builders<Appeal>.Filter.Eq(a => a.OutboxLeaseOwner, owner)
                   & Builders<Appeal>.Filter.ElemMatch(a => a.Outbox,
                         m => m.Id == message.Id && m.Status == AppealOutboxStatus.Pending);
        var update = Builders<Appeal>.Update
            .Set(a => a.Outbox!.FirstMatchingElement().Status, message.Status)
            .Set(a => a.Outbox!.FirstMatchingElement().Attempts, message.Attempts)
            .Set(a => a.Outbox!.FirstMatchingElement().NextAttemptAt, message.NextAttemptAt)
            .Set(a => a.Outbox!.FirstMatchingElement().LastError, message.LastError)
            .Set(a => a.Outbox!.FirstMatchingElement().LastAttemptAt, message.LastAttemptAt)
            .Set(a => a.Outbox!.FirstMatchingElement().CompletedAt, message.CompletedAt)
            .Set(a => a.Outbox!.FirstMatchingElement().ExpiresAt, message.ExpiresAt);
        var result = await _appeals.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task ReleaseLeaseAsync(
        string tenantId, string appealId, string owner, IReadOnlyCollection<string> pruneIds, CancellationToken ct = default)
    {
        await _appeals.UpdateOneAsync(
            ById(tenantId, appealId) & Builders<Appeal>.Filter.Eq(a => a.OutboxLeaseOwner, owner),
            Builders<Appeal>.Update.Unset(a => a.OutboxLeaseOwner).Unset(a => a.OutboxLeaseUntil),
            cancellationToken: ct);

        if (pruneIds.Count == 0) return;
        var ids = pruneIds.ToList();
        await _appeals.UpdateOneAsync(
            ById(tenantId, appealId),
            Builders<Appeal>.Update.PullFilter(a => a.Outbox,
                m => ids.Contains(m.Id) && m.Status != AppealOutboxStatus.Pending),
            cancellationToken: ct);
    }

    private static readonly BsonDocument RequeueSet = new("$set", new BsonDocument
    {
        { "Outbox.$[e].Status", nameof(AppealOutboxStatus.Pending) },
        { "Outbox.$[e].Attempts", 0 },
        { "Outbox.$[e].NextAttemptAt", BsonNull.Value },
        { "Outbox.$[e].CompletedAt", BsonNull.Value },
        { "Outbox.$[e].ExpiresAt", BsonNull.Value }
    });

    private static BsonDocument DeadLetterElement(string? eventId)
    {
        var element = new BsonDocument("e.Status", nameof(AppealOutboxStatus.DeadLettered));
        if (eventId is not null)
            element.Add("$or", new BsonArray
            {
                new BsonDocument("e.EventId", eventId),
                new BsonDocument("e.IdempotencyKey", eventId)
            });
        return element;
    }

    private static int CountDeadLetters(IEnumerable<Appeal> appeals, string? eventId) =>
        appeals.Sum(a => a.Outbox?.Count(m => m.Status == AppealOutboxStatus.DeadLettered
                                              && (eventId is null || m.EventId == eventId || m.IdempotencyKey == eventId)) ?? 0);

    public async Task<int> RequeueDeadLetteredAsync(
        string tenantId, string appealId, string? eventId, CancellationToken ct = default)
    {
        var filter = ById(tenantId, appealId)
                   & Builders<Appeal>.Filter.Eq(StatusPath, nameof(AppealOutboxStatus.DeadLettered));
        var appeals = await _appeals.Find(filter).ToListAsync(ct);
        var count = CountDeadLetters(appeals, eventId);
        if (count == 0) return 0;
        await _appeals.UpdateOneAsync(filter, new BsonDocumentUpdateDefinition<Appeal>(RequeueSet),
            new UpdateOptions
            {
                ArrayFilters = new[] { new BsonDocumentArrayFilterDefinition<BsonDocument>(DeadLetterElement(eventId)) }
            }, ct);
        return count;
    }

    public async Task<int> RequeueAllDeadLetteredAsync(string? tenantId, CancellationToken ct = default)
    {
        var filter = Builders<Appeal>.Filter.Eq(StatusPath, nameof(AppealOutboxStatus.DeadLettered));
        if (tenantId is not null) filter &= Builders<Appeal>.Filter.Eq(a => a.TenantId, tenantId);
        var appeals = await _appeals.Find(filter).Project<Appeal>(Builders<Appeal>.Projection.Include(a => a.Outbox))
            .ToListAsync(ct);
        var count = CountDeadLetters(appeals, null);
        if (count == 0) return 0;
        await _appeals.UpdateManyAsync(filter, new BsonDocumentUpdateDefinition<Appeal>(RequeueSet),
            new UpdateOptions
            {
                ArrayFilters = new[] { new BsonDocumentArrayFilterDefinition<BsonDocument>(DeadLetterElement(null)) }
            }, ct);
        return count;
    }

    public async Task<AppealOutboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        var pending = new BsonDocument(StatusPath, nameof(AppealOutboxStatus.Pending));
        var pipeline = new[]
        {
            new BsonDocument("$match", pending),
            new BsonDocument("$unwind", "$Outbox"),
            new BsonDocument("$match", pending),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "count", new BsonDocument("$sum", 1) },
                { "oldest", new BsonDocument("$min", "$Outbox.CreatedAt") }
            })
        };
        var raw = _appeals.Database.GetCollection<BsonDocument>(AppealsCollectionName);
        var row = await (await raw.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct)).FirstOrDefaultAsync(ct);
        return row is null
            ? new AppealOutboxStats(0, null)
            : new AppealOutboxStats(row["count"].ToInt64(), row["oldest"].IsBsonNull ? null : row["oldest"].ToUniversalTime());
    }
}
