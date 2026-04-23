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
/// </summary>
public sealed class AppealRepository : IAppealRepository
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

        try
        {
            var fresh = await _appeals.ReadItemAsync<Appeal>(
                appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);

            if (fresh.Resource.Status != expectedFromStatus)
            {
                throw new InvalidAppealTransitionException(fresh.Resource.Status, appeal.Status);
            }

            appeal.UpdatedAt = DateTime.UtcNow;
            var options = new ItemRequestOptions { IfMatchEtag = fresh.ETag };
            var response = await _appeals.ReplaceItemAsync(
                appeal, appeal.Id, new PartitionKey(appeal.TenantId), options, ct);

            await _events.AppendAsync(auditEvent, ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidAppealTransitionException(expectedFromStatus, appeal.Status);
        }
    }

    public async Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        try
        {
            var fresh = await _appeals.ReadItemAsync<Appeal>(
                appeal.Id, new PartitionKey(appeal.TenantId), cancellationToken: ct);

            if (fresh.Resource.OverdueAuditEmitted) return null;
            if (fresh.Resource.Status != AppealStatus.Submitted &&
                fresh.Resource.Status != AppealStatus.InReview &&
                fresh.Resource.Status != AppealStatus.PendingInfo)
            {
                return null;
            }

            var mutated = fresh.Resource;
            mutated.OverdueAuditEmitted = true;
            mutated.UpdatedAt = DateTime.UtcNow;

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
                mutated.Notes.Add(note);
                mutated.UpdatedAt = DateTime.UtcNow;

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
                mutated.Attachments.Add(attachment);
                if (!string.IsNullOrEmpty(attachment.ControlNumber))
                    mutated.AttachmentControlNumbers.Add(attachment.ControlNumber);
                mutated.UpdatedAt = DateTime.UtcNow;

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
                var attachment = mutated.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId)
                    ?? throw new InvalidOperationException(
                        $"Attachment {attachmentId} not found on appeal {appealId} for tenant {tenantId}.");

                attachment.AcknowledgmentReceived = acknowledgmentReceived;
                attachment.Status = acknowledgmentReceived ? AttachmentStatus.Acknowledged : AttachmentStatus.Sent;
                if (acknowledgmentReceived) attachment.SentDate = DateTime.UtcNow;
                mutated.UpdatedAt = DateTime.UtcNow;

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
                mutated.AssignedReviewerId = appeal.AssignedReviewerId;
                mutated.UpdatedAt = DateTime.UtcNow;

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
}
