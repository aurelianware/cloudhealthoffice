using System.Net;
using System.Text.Json;
using AppealsService.Models;
using Microsoft.Azure.Cosmos;

namespace AppealsService.Repositories;

/// <summary>
/// Cosmos DB implementation of <see cref="IAppealRepository"/>.
/// Partition key: <c>/tenantId</c>. Audit-trail atomicity for
/// <see cref="TransitionStatusAsync"/> / <see cref="TryTransitionToOverdueAsync"/>
/// follows the same pattern as consent-service and personal-rep-service:
/// conditional ReplaceItem with ETag precondition on the appeal entity;
/// the audit event is appended after the conditional replace succeeds.
/// Event writes are idempotent (unique key on EventId) so a retry is safe.
///
/// The Kafka event rides in the appeal document itself
/// (<see cref="Appeal.Outbox"/>), added to the same ETag-pinned
/// create / replace as the change. One document is one atomic write — a
/// stronger guarantee than a same-partition transactional batch, and no
/// second container is involved. Also the <see cref="IAppealOutboxStore"/>
/// the dispatcher drains (cross-partition query for pending entries).
/// </summary>
public sealed class AppealRepository : IAppealRepository, IAppealOutboxStore
{
    public const string AppealsContainerName = "Appeals";

    private readonly Container _appeals;
    private readonly IAppealEventSink _events;

    public AppealRepository(CosmosClient cosmosClient, string databaseName, IAppealEventSink events)
    {
        _appeals = cosmosClient.GetDatabase(databaseName).GetContainer(AppealsContainerName);
        _events = events;
    }

    /// <summary>
    /// Marshal an enum value into the on-disk Cosmos representation.
    /// <see cref="Middleware.CosmosSystemTextJsonSerializer"/> registers
    /// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c>, so
    /// <c>AppealStatus.Closed</c> persists as <c>"closed"</c>, not
    /// <c>"Closed"</c>. SQL parameters compared against <c>c.status</c>
    /// (and other enum-valued document fields) MUST use this helper —
    /// raw <c>.ToString()</c> silently never matches.
    /// </summary>
    private static string CosmosEnumValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public async Task<Appeal> CreateAsync(Appeal appeal, AppealEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appeal.Id)) appeal.Id = Guid.NewGuid().ToString();
        if (appeal.CreatedAt == default) appeal.CreatedAt = DateTime.UtcNow;
        AddOutbox(appeal, genesisEvent);

        var response = await _appeals.CreateItemAsync(appeal, new PartitionKey(appeal.TenantId), cancellationToken: ct);
        await _events.AppendAsync(genesisEvent, ct);
        return response.Resource;
    }

    public async Task<Appeal?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            var response = await _appeals.ReadItemAsync<Appeal>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Appeal?> GetByAppealNumberAsync(string tenantId, string appealNumber, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.appealNumber = @appealNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@appealNumber", appealNumber);

        var iterator = _appeals.GetItemQueryIterator<Appeal>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var item in page) return item;
        }
        return null;
    }

    public async Task<IReadOnlyList<Appeal>> GetByClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        var iterator = _appeals.GetItemQueryIterator<Appeal>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<Appeal>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<Appeal?> GetMostRecentAppealByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c " +
            "WHERE c.tenantId = @tenantId AND c.claimId = @claimId AND c.status != @closed " +
            "ORDER BY c.submittedDate DESC OFFSET 0 LIMIT 1")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId)
            .WithParameter("@closed", CosmosEnumValue(AppealStatus.Closed));

        var iterator = _appeals.GetItemQueryIterator<Appeal>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var item in page) return item;
        }
        return null;
    }

    public async Task<IReadOnlyList<Appeal>> SearchAsync(
        string tenantId, AppealSearchParams p, CancellationToken ct = default)
    {
        var page = Math.Max(1, p.Page);
        var pageSize = Math.Clamp(p.PageSize, 1, 100);

        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(p.MemberId))
        {
            queryText += " AND c.memberId = @memberId";
            parameters.Add(("@memberId", p.MemberId));
        }
        if (!string.IsNullOrEmpty(p.ProviderNPI))
        {
            queryText += " AND c.providerNPI = @providerNPI";
            parameters.Add(("@providerNPI", p.ProviderNPI));
        }
        if (p.SubmittedFrom.HasValue)
        {
            queryText += " AND c.submittedDate >= @submittedFrom";
            parameters.Add(("@submittedFrom", p.SubmittedFrom.Value));
        }
        if (p.SubmittedTo.HasValue)
        {
            queryText += " AND c.submittedDate <= @submittedTo";
            parameters.Add(("@submittedTo", p.SubmittedTo.Value));
        }
        if (p.Status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", CosmosEnumValue(p.Status.Value)));
        }
        if (p.ClosureReasonCode.HasValue)
        {
            queryText += " AND c.closureReasonCode = @closureReasonCode";
            parameters.Add(("@closureReasonCode", CosmosEnumValue(p.ClosureReasonCode.Value)));
        }
        if (p.LineOfBusiness.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            parameters.Add(("@lineOfBusiness", CosmosEnumValue(p.LineOfBusiness.Value)));
        }
        if (!string.IsNullOrEmpty(p.AssignedReviewerId))
        {
            queryText += " AND c.assignedReviewerId = @assignedReviewerId";
            parameters.Add(("@assignedReviewerId", p.AssignedReviewerId));
        }

        queryText += " ORDER BY c.createdAt DESC";
        // page and pageSize are bounded integers — no injection surface.
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var query = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters) query = query.WithParameter(name, value);

        var iterator = _appeals.GetItemQueryIterator<Appeal>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<Appeal>();
        while (iterator.HasMoreResults)
        {
            var pageResults = await iterator.ReadNextAsync(ct);
            results.AddRange(pageResults);
        }
        return results;
    }

    public async Task<AppealsSummary> GetAppealsSummaryAsync(
        string tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // TODO(appeals-followup-summary-perf): scan-all + in-memory aggregation
        // scales poorly. Migrate to a Cosmos GROUP BY in a follow-up PR.
        var results = await SearchAsync(
            tenantId,
            new AppealSearchParams { SubmittedFrom = from, SubmittedTo = to, Page = 1, PageSize = 100 },
            ct);

        // Paginate — SearchAsync caps at 100/page. Keep reading for the summary.
        var all = new List<Appeal>(results);
        var page = 2;
        while (results.Count == 100)
        {
            results = await SearchAsync(
                tenantId,
                new AppealSearchParams { SubmittedFrom = from, SubmittedTo = to, Page = page, PageSize = 100 },
                ct);
            all.AddRange(results);
            page++;
        }

        return SummaryBuilder.Build(all);
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

        // The replacement is the fresh read with only the transition-owned
        // fields applied, pinned to that read's ETag; the caller's snapshot
        // contributes nothing else, so a concurrent attachment, note,
        // reviewer assignment, extension or overdue observation survives.
        // A 412 means some other write landed after the read: re-read and
        // rebuild (re-checking the status) rather than fail the transition.
        for (var attempt = 0; attempt < TransitionAttempts; attempt++)
        {
            var fresh = await _appeals.ReadItemAsync<Appeal>(
                appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);

            if (fresh.Resource.Status != expectedFromStatus)
            {
                throw new InvalidAppealTransitionException(fresh.Resource.Status, appeal.Status);
            }

            appeal.UpdatedAt = DateTime.UtcNow;
            var mutated = fresh.Resource;
            AppealStatusTransitionFields.CopyTo(appeal, mutated);
            AddOutbox(mutated, auditEvent);

            ItemResponse<Appeal> response;
            try
            {
                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                response = await _appeals.ReplaceItemAsync(
                    mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }

            await _events.AppendAsync(auditEvent, ct);
            return response.Resource;
        }

        throw new InvalidAppealTransitionException(expectedFromStatus, appeal.Status);
    }

    /// <summary>
    /// Read-rebuild-replace attempts for a status transition. Each 412 is a
    /// concurrent write to the same appeal; a handful of retries absorbs a
    /// burst (e.g. several 275 attachments) before reporting a conflict.
    /// </summary>
    private const int TransitionAttempts = 5;

    public async Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        try
        {
            var fresh = await _appeals.ReadItemAsync<Appeal>(
                appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);

            if (fresh.Resource.OverdueAuditEmitted) return null;
            // The event payload (built before the write) reports the caller's status.
            if (fresh.Resource.Status != appeal.Status) return null;
            if (fresh.Resource.Status != AppealStatus.Submitted &&
                fresh.Resource.Status != AppealStatus.InReview &&
                fresh.Resource.Status != AppealStatus.PendingInfo)
            {
                return null;
            }

            var mutated = fresh.Resource;
            mutated.OverdueAuditEmitted = true;
            mutated.UpdatedAt = DateTime.UtcNow;
            AddOutbox(mutated, auditEvent);

            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            var response = await _appeals.ReplaceItemAsync(
                mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);

            await _events.AppendAsync(auditEvent, ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<Appeal?> TryExtendDeadlineAsync(
        Appeal appeal, AppealNote? justificationNote,
        Func<Appeal, IReadOnlyList<AppealEvent>> buildAuditEvents,
        CancellationToken ct = default)
    {
        Appeal persisted;
        try
        {
            var fresh = await _appeals.ReadItemAsync<Appeal>(
                appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);
            persisted = fresh.Resource;

            if (persisted.DeadlineExtension is null)
            {
                if (persisted.Status != AppealStatus.Submitted &&
                    persisted.Status != AppealStatus.InReview &&
                    persisted.Status != AppealStatus.PendingInfo)
                {
                    return null;
                }

                persisted.TargetResponseDate = appeal.TargetResponseDate;
                persisted.DeadlineExtension = appeal.DeadlineExtension;
                if (justificationNote is not null) persisted.Notes.Add(justificationNote);
                persisted.UpdatedAt = appeal.UpdatedAt ?? DateTime.UtcNow;
                persisted.UpdatedBy = appeal.UpdatedBy;
                // Built from the proposed extension, which is what this
                // write persists; a replay writes nothing.
                (persisted.Outbox ??= new()).AddRange(
                    AppealsService.Services.AppealOutbox.MessagesOf(buildAuditEvents(appeal)));
                AppealOutboxIndex.Refresh(persisted);

                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                var response = await _appeals.ReplaceItemAsync(
                    persisted, persisted.Id, new PartitionKey(persisted.TenantId), options, ct);
                persisted = response.Resource;
            }
            else if (!IsSameExtension(persisted, appeal))
            {
                return null;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null;
        }

        foreach (var evt in buildAuditEvents(persisted)) await _events.AppendAsync(evt, ct);
        return persisted;
    }

    internal static bool IsSameExtension(Appeal persisted, Appeal requested) =>
        persisted.DeadlineExtension?.EventId is { Length: > 0 } stored
        && stored == requested.DeadlineExtension?.EventId;

    public async Task<Appeal> AppendNoteAsync(Appeal appeal, AppealNote note, AppealEvent auditEvent, CancellationToken ct = default)
    {
        // Cosmos has no native array-push operator for arbitrary depth. We
        // re-read with ETag and do a conditional ReplaceItem. Contention on
        // the same appeal's notes is rare; on 412 we retry once. A third
        // conflicting writer is extremely unlikely in practice.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var fresh = await _appeals.ReadItemAsync<Appeal>(
                    appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);
                var mutated = fresh.Resource;
                if (IsReplay(mutated, auditEvent))
                {
                    await _events.AppendAsync(auditEvent, ct);
                    return mutated;
                }
                mutated.Notes.Add(note);
                mutated.UpdatedAt = DateTime.UtcNow;
                AddOutbox(mutated, auditEvent);

                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                var response = await _appeals.ReplaceItemAsync(
                    mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);

                await _events.AppendAsync(auditEvent, ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 1) throw;
            }
        }
        throw new InvalidOperationException("AppendNoteAsync retry budget exhausted.");
    }

    public async Task<Appeal> AppendAttachmentAsync(Appeal appeal, AppealAttachment attachment, AppealEvent auditEvent, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var fresh = await _appeals.ReadItemAsync<Appeal>(
                    appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);
                var mutated = fresh.Resource;
                if (IsReplay(mutated, auditEvent))
                {
                    await _events.AppendAsync(auditEvent, ct);
                    return mutated;
                }
                mutated.Attachments.Add(attachment);
                if (!string.IsNullOrEmpty(attachment.ControlNumber))
                    mutated.AttachmentControlNumbers.Add(attachment.ControlNumber);
                mutated.UpdatedAt = DateTime.UtcNow;
                AddOutbox(mutated, auditEvent);

                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                var response = await _appeals.ReplaceItemAsync(
                    mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);

                await _events.AppendAsync(auditEvent, ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 1) throw;
            }
        }
        throw new InvalidOperationException("AppendAttachmentAsync retry budget exhausted.");
    }

    public async Task<Appeal> AcknowledgeAttachmentAsync(
        string tenantId, string appealId, string attachmentId, bool acknowledgmentReceived,
        AppealEvent auditEvent, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var fresh = await _appeals.ReadItemAsync<Appeal>(
                    appealId, new PartitionKey(tenantId), cancellationToken: ct);
                var mutated = fresh.Resource;
                if (IsReplay(mutated, auditEvent))
                {
                    await _events.AppendAsync(auditEvent, ct);
                    return mutated;
                }
                var attachment = mutated.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId)
                    ?? throw new InvalidOperationException(
                        $"Attachment {attachmentId} not found on appeal {appealId} for tenant {tenantId}.");

                attachment.AcknowledgmentReceived = acknowledgmentReceived;
                attachment.Status = acknowledgmentReceived ? AttachmentStatus.Acknowledged : AttachmentStatus.Sent;
                if (acknowledgmentReceived) attachment.SentDate = auditEvent.OccurredAt;
                mutated.UpdatedAt = DateTime.UtcNow;
                AddOutbox(mutated, auditEvent);

                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                var response = await _appeals.ReplaceItemAsync(
                    mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);

                await _events.AppendAsync(auditEvent, ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 1) throw;
            }
        }
        throw new InvalidOperationException("AcknowledgeAttachmentAsync retry budget exhausted.");
    }

    public async Task<Appeal> AssignReviewerAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var fresh = await _appeals.ReadItemAsync<Appeal>(
                    appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);
                var mutated = fresh.Resource;
                if (IsReplay(mutated, auditEvent))
                {
                    await _events.AppendAsync(auditEvent, ct);
                    return mutated;
                }
                mutated.AssignedReviewerId = appeal.AssignedReviewerId;
                mutated.UpdatedAt = DateTime.UtcNow;
                AddOutbox(mutated, auditEvent);

                var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
                var response = await _appeals.ReplaceItemAsync(
                    mutated, mutated.Id, new PartitionKey(mutated.TenantId), options, ct);

                await _events.AppendAsync(auditEvent, ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 1) throw;
            }
        }
        throw new InvalidOperationException("AssignReviewerAsync retry budget exhausted.");
    }

    public async Task<AppealNoteLookup?> GetNoteByIdAsync(string tenantId, string noteId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND EXISTS(SELECT VALUE n FROM n IN c.notes WHERE n.noteId = @noteId)")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@noteId", noteId);

        using var iterator = _appeals.GetItemQueryIterator<Appeal>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            var appeal = page.FirstOrDefault();
            if (appeal is null) continue;
            var note = appeal.Notes.FirstOrDefault(n => n.NoteId == noteId);
            if (note is null) continue;
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
        return null;
    }

    public async Task<AppealAttachmentLookup?> GetAttachmentByIdAsync(string tenantId, string attachmentId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND EXISTS(SELECT VALUE a FROM a IN c.attachments WHERE a.attachmentId = @attachmentId)")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@attachmentId", attachmentId);

        using var iterator = _appeals.GetItemQueryIterator<Appeal>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            var appeal = page.FirstOrDefault();
            if (appeal is null) continue;
            var att = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
            if (att is null) continue;
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
        return null;
    }

    /// <summary>Adds the event's outbox entry (required) and refreshes the sweep fields, inside the same replace as the change.</summary>
    private static void AddOutbox(Appeal target, AppealEvent auditEvent)
    {
        (target.Outbox ??= new()).Add(AppealsService.Services.AppealOutbox.Require(auditEvent));
        AppealOutboxIndex.Refresh(target);
    }

    /// <summary>The idempotency key is already in the outbox: this call is a replay and appends nothing.</summary>
    private static bool IsReplay(Appeal persisted, AppealEvent auditEvent)
    {
        var key = AppealsService.Services.AppealOutbox.Require(auditEvent).IdempotencyKey;
        return persisted.Outbox?.Any(m => m.IdempotencyKey == key) == true;
    }

    // ── IAppealOutboxStore ──────────────────────────────────────────────

    /// <summary>ETag-pinned read-modify-replace attempts for outbox bookkeeping.</summary>
    private const int OutboxAttempts = 5;

    public async Task<IReadOnlyList<AppealOutboxKey>> FindDueAsync(DateTime now, int limit, CancellationToken ct = default)
    {
        // Top-level numeric fields maintained in every write
        // (AppealOutboxIndex): the query filters and orders on scalars, so
        // appeals whose only entries are backing off are not returned and
        // cannot starve due ones.
        var nowMs = AppealOutboxIndex.ToEpochMs(now);
        var query = new QueryDefinition(
            "SELECT c.tenantId, c.id FROM c " +
            "WHERE IS_NUMBER(c.outboxNextDueAt) AND c.outboxNextDueAt <= @now " +
            "AND (NOT IS_NUMBER(c.outboxLeaseUntilMs) OR c.outboxLeaseUntilMs < @now) " +
            "ORDER BY c.outboxNextDueAt OFFSET 0 LIMIT @limit")
            .WithParameter("@now", nowMs)
            .WithParameter("@limit", limit);
        return await QueryKeysAsync(query, partition: null, ct);
    }

    private async Task<List<AppealOutboxKey>> QueryKeysAsync(QueryDefinition query, string? partition, CancellationToken ct)
    {
        var results = new List<AppealOutboxKey>();
        var options = partition is null ? null : new QueryRequestOptions { PartitionKey = new PartitionKey(partition) };
        using var iterator = _appeals.GetItemQueryIterator<OutboxKeyRow>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var row in await iterator.ReadNextAsync(ct))
                results.Add(new AppealOutboxKey(row.TenantId, row.Id));
        }
        return results;
    }

    /// <summary>Projection row for outbox sweep queries (public so tests can mock the iterator).</summary>
    public sealed class OutboxKeyRow
    {
        public string TenantId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>
    /// Read, apply <paramref name="mutate"/> (false = nothing to write),
    /// refresh the sweep fields, replace pinned to the read's ETag; a 412
    /// re-reads and retries. Returns the persisted appeal, or null when not
    /// found / not written.
    /// </summary>
    private async Task<Appeal?> MutateOutboxAsync(
        string tenantId, string appealId, Func<Appeal, bool> mutate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < OutboxAttempts; attempt++)
        {
            ItemResponse<Appeal> fresh;
            try
            {
                fresh = await _appeals.ReadItemAsync<Appeal>(appealId, new PartitionKey(tenantId), cancellationToken: ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var appeal = fresh.Resource;
            if (!mutate(appeal)) return null;
            AppealOutboxIndex.Refresh(appeal);
            try
            {
                var response = await _appeals.ReplaceItemAsync(
                    appeal, appeal.Id, new PartitionKey(appeal.TenantId),
                    new ItemRequestOptions { IfMatchEtag = fresh.ETag }, ct);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // A concurrent appeal change; it carried the outbox along. Retry on it.
            }
        }
        throw new InvalidOperationException($"Outbox update for appeal {appealId} kept conflicting.");
    }

    public async Task<AppealOutboxLease?> TryLeaseAsync(
        string tenantId, string appealId, string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default)
    {
        var leased = await MutateOutboxAsync(tenantId, appealId, a =>
        {
            if (a.OutboxLeaseUntil is { } until && until >= now && a.OutboxLeaseOwner != owner) return false;
            if (!AppealOutboxIndex.HasDueWork(a.Outbox, now)) return false; // nothing due: no write
            a.OutboxLeaseOwner = owner;
            a.OutboxLeaseUntil = leaseUntil;
            return true;
        }, ct);
        return leased is null
            ? null
            : new AppealOutboxLease(leased.Outbox ?? new List<AppealOutboxMessage>(), leased.OutboxSequence ?? 0);
    }

    public async Task<bool> RenewLeaseAsync(
        string tenantId, string appealId, string owner, DateTime leaseUntil,
        AppealOutboxSequenceAssignment? assign, CancellationToken ct = default) =>
        await MutateOutboxAsync(tenantId, appealId, a =>
        {
            if (a.OutboxLeaseOwner != owner) return false;
            if (assign is not null)
            {
                var entry = a.Outbox?.FirstOrDefault(m => m.Id == assign.EntryId);
                if (entry is null || entry.Sequence is not null || (a.OutboxSequence ?? 0) != assign.Sequence - 1) return false;
                entry.Sequence = assign.Sequence;
                a.OutboxSequence = assign.Sequence;
            }
            a.OutboxLeaseUntil = leaseUntil;
            return true;
        }, ct) is not null;

    public async Task<bool> UpdateMessageAsync(
        string tenantId, string appealId, string owner, AppealOutboxMessage message, CancellationToken ct = default) =>
        await MutateOutboxAsync(tenantId, appealId, a =>
        {
            if (a.OutboxLeaseOwner != owner) return false;
            var entry = a.Outbox?.FirstOrDefault(m => m.Id == message.Id && m.Status == AppealOutboxStatus.Pending);
            if (entry is null) return false;
            CopyOutcome(message, entry);
            return true;
        }, ct) is not null;

    internal static void CopyOutcome(AppealOutboxMessage from, AppealOutboxMessage to)
    {
        to.Status = from.Status;
        to.Attempts = from.Attempts;
        to.NextAttemptAt = from.NextAttemptAt;
        to.LastError = from.LastError;
        to.LastAttemptAt = from.LastAttemptAt;
        to.CompletedAt = from.CompletedAt;
        to.ExpiresAt = from.ExpiresAt;
    }

    public Task ReleaseLeaseAsync(
        string tenantId, string appealId, string owner, IReadOnlyCollection<string> pruneIds, CancellationToken ct = default) =>
        MutateOutboxAsync(tenantId, appealId, a =>
        {
            var changed = false;
            if (a.OutboxLeaseOwner == owner)
            {
                a.OutboxLeaseOwner = null;
                a.OutboxLeaseUntil = null;
                changed = true;
            }
            var pruned = a.Outbox?.RemoveAll(m => pruneIds.Contains(m.Id) && m.Status != AppealOutboxStatus.Pending) ?? 0;
            return changed || pruned > 0;
        }, ct);

    internal static int Requeue(Appeal a, string? eventId)
    {
        var count = 0;
        foreach (var m in a.Outbox?.Where(m => m.Status == AppealOutboxStatus.DeadLettered
                                               && (eventId is null || m.EventId == eventId || m.IdempotencyKey == eventId))
                          ?? Enumerable.Empty<AppealOutboxMessage>())
        {
            m.Status = AppealOutboxStatus.Pending;
            m.Attempts = 0;
            m.NextAttemptAt = null;
            m.CompletedAt = null;
            m.ExpiresAt = null;
            count++;
        }
        return count;
    }

    public async Task<int> RequeueDeadLetteredAsync(
        string tenantId, string appealId, string? eventId, CancellationToken ct = default)
    {
        var requeued = 0;
        await MutateOutboxAsync(tenantId, appealId, a => (requeued = Requeue(a, eventId)) > 0, ct);
        return requeued;
    }

    public async Task<int> RequeueAllDeadLetteredAsync(string? tenantId, CancellationToken ct = default)
    {
        var text = "SELECT c.tenantId, c.id FROM c WHERE c.outboxDeadLetteredCount > 0";
        var query = new QueryDefinition(tenantId is null ? text : text + " AND c.tenantId = @tenantId");
        if (tenantId is not null) query = query.WithParameter("@tenantId", tenantId);
        var total = 0;
        foreach (var key in await QueryKeysAsync(query, tenantId, ct))
            total += await RequeueDeadLetteredAsync(key.TenantId, key.AppealId, eventId: null, ct);
        return total;
    }

    public async Task<AppealOutboxStats> GetStatsAsync(CancellationToken ct = default)
    {
        var pending = await ScalarAsync(
            "SELECT VALUE SUM(c.outboxPendingCount) FROM c WHERE IS_NUMBER(c.outboxPendingCount)", ct);
        var oldest = await ScalarAsync(
            "SELECT VALUE MIN(c.outboxOldestPendingAt) FROM c WHERE IS_NUMBER(c.outboxOldestPendingAt)", ct);
        return new AppealOutboxStats(
            (long)(pending ?? 0),
            oldest is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime : null);
    }

    private async Task<double?> ScalarAsync(string sql, CancellationToken ct)
    {
        double? value = null;
        using var iterator = _appeals.GetItemQueryIterator<double?>(new QueryDefinition(sql));
        while (iterator.HasMoreResults)
            foreach (var v in await iterator.ReadNextAsync(ct))
                if (v is not null) value = v;
        return value;
    }
}
