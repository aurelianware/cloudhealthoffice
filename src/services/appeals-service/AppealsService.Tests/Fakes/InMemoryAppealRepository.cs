using System.Collections.Concurrent;
using AppealsService.Models;
using AppealsService.Repositories;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IAppealRepository"/> + <see cref="IAppealEventRepository"/>
/// + <see cref="IAppealEventSink"/> triple. Shared across controller and
/// integration tests. Not a production substitute — the concurrency
/// primitives here are sufficient to exercise the race-safe overdue path
/// and transition-replace conditional but don't reproduce Cosmos/Mongo
/// operator semantics exactly.
///
/// Exposes <see cref="FailAuditAppendOnce"/> as a failure-injection hook
/// for atomicity tests: one-shot flag, cleared after the next append
/// attempt regardless of whether it succeeded or threw.
/// </summary>
public sealed class InMemoryAppealRepository : IAppealRepository, IAppealEventRepository, IAppealEventSink
{
    private readonly ConcurrentDictionary<string, Appeal> _appeals = new();
    private readonly ConcurrentBag<AppealEvent> _events = new();
    private readonly object _sync = new();
    private bool _failAuditAppendOnce;

    private static string Key(string tenantId, string id) => $"{tenantId}:{id}";

    public void FailAuditAppendOnce() => _failAuditAppendOnce = true;

    /// <summary>
    /// Reset internal state in place. Used by the
    /// <see cref="Integration.AppealsWebApplicationFactory"/>'s test-scoped
    /// Reset hook — replacing the whole fake between tests would desync
    /// the DI container (which caches the singleton at first build).
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _appeals.Clear();
            while (_events.TryTake(out _)) { }
            _failAuditAppendOnce = false;
        }
    }

    public Task<Appeal> CreateAsync(Appeal appeal, AppealEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appeal.Id)) appeal.Id = Guid.NewGuid().ToString();
        if (appeal.CreatedAt == default) appeal.CreatedAt = DateTime.UtcNow;

        lock (_sync)
        {
            var clone = Clone(appeal);
            _appeals[Key(appeal.TenantId, appeal.Id)] = clone;
            AppendEventInternal(genesisEvent);
        }
        return Task.FromResult(Clone(appeal));
    }

    public Task<Appeal?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _appeals.TryGetValue(Key(tenantId, id), out var a);
        return Task.FromResult<Appeal?>(a is null ? null : Clone(a));
    }

    public Task<Appeal?> GetByAppealNumberAsync(string tenantId, string appealNumber, CancellationToken ct = default)
    {
        var match = _appeals.Values.FirstOrDefault(a => a.TenantId == tenantId && a.AppealNumber == appealNumber);
        return Task.FromResult<Appeal?>(match is null ? null : Clone(match));
    }

    public Task<IReadOnlyList<Appeal>> GetByClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var results = _appeals.Values
            .Where(a => a.TenantId == tenantId && a.ClaimId == claimId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<Appeal>>(results);
    }

    public Task<Appeal?> GetMostRecentAppealByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        var match = _appeals.Values
            .Where(a => a.TenantId == tenantId
                     && a.ClaimId == claimId
                     && a.Status != AppealStatus.Closed)
            .OrderByDescending(a => a.SubmittedDate)
            .FirstOrDefault();
        return Task.FromResult<Appeal?>(match is null ? null : Clone(match));
    }

    public Task<IReadOnlyList<Appeal>> SearchAsync(string tenantId, AppealSearchParams p, CancellationToken ct = default)
    {
        var query = _appeals.Values.Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrEmpty(p.MemberId)) query = query.Where(a => a.MemberId == p.MemberId);
        if (!string.IsNullOrEmpty(p.ProviderNPI)) query = query.Where(a => a.ProviderNPI == p.ProviderNPI);
        if (p.SubmittedFrom.HasValue) query = query.Where(a => a.SubmittedDate >= p.SubmittedFrom.Value);
        if (p.SubmittedTo.HasValue) query = query.Where(a => a.SubmittedDate <= p.SubmittedTo.Value);
        if (p.Status.HasValue) query = query.Where(a => a.Status == p.Status.Value);
        if (p.ClosureReasonCode.HasValue) query = query.Where(a => a.ClosureReasonCode == p.ClosureReasonCode.Value);
        if (p.LineOfBusiness.HasValue) query = query.Where(a => a.LineOfBusiness == p.LineOfBusiness.Value);
        if (!string.IsNullOrEmpty(p.AssignedReviewerId))
            query = query.Where(a => a.AssignedReviewerId == p.AssignedReviewerId);

        var page = Math.Max(1, p.Page);
        var pageSize = Math.Clamp(p.PageSize, 1, 100);
        var results = query.OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<Appeal>>(results);
    }

    public async Task<AppealsSummary> GetAppealsSummaryAsync(
        string tenantId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var rows = _appeals.Values
            .Where(a => a.TenantId == tenantId && a.SubmittedDate >= from && a.SubmittedDate <= to)
            .Select(Clone)
            .ToList();
        return await Task.FromResult(SummaryBuilder.Build(rows));
    }

    public Task<Appeal> TransitionStatusAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expected = auditEvent.FromStatus.Value;

        lock (_sync)
        {
            var key = Key(appeal.TenantId, appeal.Id);
            if (!_appeals.TryGetValue(key, out var current) || current.Status != expected)
            {
                var actual = current?.Status ?? appeal.Status;
                throw new InvalidAppealTransitionException(actual, appeal.Status);
            }

            _appeals[key] = Clone(appeal);
            AppendEventInternal(auditEvent);
        }
        return Task.FromResult(Clone(appeal));
    }

    public Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var key = Key(appeal.TenantId, appeal.Id);
            if (!_appeals.TryGetValue(key, out var current)) return Task.FromResult<Appeal?>(null);
            if (current.OverdueAuditEmitted) return Task.FromResult<Appeal?>(null);
            if (current.Status != AppealStatus.Submitted
                && current.Status != AppealStatus.InReview
                && current.Status != AppealStatus.PendingInfo)
                return Task.FromResult<Appeal?>(null);

            current.OverdueAuditEmitted = true;
            current.UpdatedAt = DateTime.UtcNow;
            _appeals[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult<Appeal?>(Clone(current));
        }
    }

    public Task<Appeal> AppendNoteAsync(Appeal appeal, AppealNote note, AppealEvent auditEvent, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var key = Key(appeal.TenantId, appeal.Id);
            if (!_appeals.TryGetValue(key, out var current))
                throw new InvalidOperationException($"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");
            current.Notes.Add(note);
            current.UpdatedAt = DateTime.UtcNow;
            _appeals[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult(Clone(current));
        }
    }

    public Task<Appeal> AppendAttachmentAsync(Appeal appeal, AppealAttachment attachment, AppealEvent auditEvent, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var key = Key(appeal.TenantId, appeal.Id);
            if (!_appeals.TryGetValue(key, out var current))
                throw new InvalidOperationException($"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");
            current.Attachments.Add(attachment);
            if (!string.IsNullOrEmpty(attachment.ControlNumber))
                current.AttachmentControlNumbers.Add(attachment.ControlNumber);
            current.UpdatedAt = DateTime.UtcNow;
            _appeals[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult(Clone(current));
        }
    }

    public Task<Appeal> AcknowledgeAttachmentAsync(
        string tenantId, string appealId, string attachmentId, bool acknowledgmentReceived,
        AppealEvent auditEvent, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var key = Key(tenantId, appealId);
            if (!_appeals.TryGetValue(key, out var current))
                throw new InvalidOperationException($"Appeal {appealId} not found for tenant {tenantId}.");
            var attachment = current.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId)
                ?? throw new InvalidOperationException(
                    $"Attachment {attachmentId} not found on appeal {appealId}.");
            attachment.AcknowledgmentReceived = acknowledgmentReceived;
            attachment.Status = acknowledgmentReceived ? AttachmentStatus.Acknowledged : AttachmentStatus.Sent;
            if (acknowledgmentReceived) attachment.SentDate = DateTime.UtcNow;
            current.UpdatedAt = DateTime.UtcNow;
            _appeals[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult(Clone(current));
        }
    }

    public Task<Appeal> AssignReviewerAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var key = Key(appeal.TenantId, appeal.Id);
            if (!_appeals.TryGetValue(key, out var current))
                throw new InvalidOperationException($"Appeal {appeal.Id} not found for tenant {appeal.TenantId}.");
            current.AssignedReviewerId = appeal.AssignedReviewerId;
            current.UpdatedAt = DateTime.UtcNow;
            _appeals[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult(Clone(current));
        }
    }

    public Task<AppealNoteLookup?> GetNoteByIdAsync(string tenantId, string noteId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            foreach (var appeal in _appeals.Values.Where(a => a.TenantId == tenantId))
            {
                var note = appeal.Notes.FirstOrDefault(n => n.NoteId == noteId);
                if (note is null) continue;
                return Task.FromResult<AppealNoteLookup?>(new AppealNoteLookup
                {
                    AppealId = appeal.Id,
                    MemberId = appeal.MemberId,
                    NoteId = note.NoteId,
                    CreatedBy = note.CreatedBy,
                    NoteText = note.NoteText,
                    IsInternal = note.IsInternal,
                    CreatedAt = note.CreatedAt
                });
            }
            return Task.FromResult<AppealNoteLookup?>(null);
        }
    }

    public Task<AppealAttachmentLookup?> GetAttachmentByIdAsync(string tenantId, string attachmentId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            foreach (var appeal in _appeals.Values.Where(a => a.TenantId == tenantId))
            {
                var att = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
                if (att is null) continue;
                return Task.FromResult<AppealAttachmentLookup?>(new AppealAttachmentLookup
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
                });
            }
            return Task.FromResult<AppealAttachmentLookup?>(null);
        }
    }

    public Task AppendAsync(AppealEvent evt, CancellationToken ct = default)
    {
        AppendEventInternal(evt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AppealEvent>> ListByAppealAsync(
        string tenantId, string appealId, CancellationToken ct = default)
    {
        var rows = _events
            .Where(e => e.TenantId == tenantId && e.AppealId == appealId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.EventId) // stable order for same-millisecond events
            .ToList();
        return Task.FromResult<IReadOnlyList<AppealEvent>>(rows);
    }

    /// <summary>Returns the full audit trail across all tenants / appeals for test assertions.</summary>
    public IReadOnlyList<AppealEvent> SnapshotEvents() =>
        _events.OrderBy(e => e.OccurredAt).ThenBy(e => e.EventId).ToList();

    /// <summary>Returns the stored (pre-decrypt) appeal for test assertions.</summary>
    public Appeal? PeekStored(string tenantId, string id) =>
        _appeals.TryGetValue(Key(tenantId, id), out var a) ? a : null;

    private void AppendEventInternal(AppealEvent evt)
    {
        lock (_sync)
        {
            if (_failAuditAppendOnce)
            {
                _failAuditAppendOnce = false;
                throw new InvalidOperationException("FailAuditAppendOnce: injected failure");
            }
            if (_events.Any(e =>
                    e.TenantId == evt.TenantId &&
                    e.AppealId == evt.AppealId &&
                    e.EventId == evt.EventId))
            {
                return;
            }
            _events.Add(CloneEvent(evt));
        }
    }

    private static Appeal Clone(Appeal a) => new()
    {
        TenantId = a.TenantId,
        Id = a.Id,
        AppealNumber = a.AppealNumber,
        ClaimId = a.ClaimId,
        ClaimNumber = a.ClaimNumber,
        MemberId = a.MemberId,
        PatientName = a.PatientName,
        ProviderNPI = a.ProviderNPI,
        ProviderName = a.ProviderName,
        DenialReasonCode = a.DenialReasonCode,
        DenialReason = a.DenialReason,
        DeniedAmount = a.DeniedAmount,
        AppealedAmount = a.AppealedAmount,
        AppealType = a.AppealType,
        AppealLevel = a.AppealLevel,
        LineOfBusiness = a.LineOfBusiness,
        Status = a.Status,
        AppealReason = a.AppealReason,
        Source = a.Source,
        Attachments = a.Attachments.Select(CloneAttachment).ToList(),
        ClinicalDocuments = a.ClinicalDocuments.Select(CloneDoc).ToList(),
        Decision = a.Decision == null ? null : CloneDecision(a.Decision),
        SubmittedDate = a.SubmittedDate,
        ReceivedDate = a.ReceivedDate,
        TargetResponseDate = a.TargetResponseDate,
        DecisionDate = a.DecisionDate,
        SubmittedBy = a.SubmittedBy,
        Notes = a.Notes.Select(CloneNote).ToList(),
        AttachmentControlNumbers = new List<string>(a.AttachmentControlNumbers),
        IsUrgent = a.IsUrgent,
        ServiceDate = a.ServiceDate,
        DiagnosisCodes = new List<string>(a.DiagnosisCodes),
        ProcedureCodes = new List<string>(a.ProcedureCodes),
        AssignedReviewerId = a.AssignedReviewerId,
        CreatedAt = a.CreatedAt,
        CreatedBy = a.CreatedBy,
        UpdatedAt = a.UpdatedAt,
        UpdatedBy = a.UpdatedBy,
        ClosedAt = a.ClosedAt,
        ClosedBy = a.ClosedBy,
        ClosureReasonCode = a.ClosureReasonCode,
        OverdueAuditEmitted = a.OverdueAuditEmitted
    };

    private static AppealAttachment CloneAttachment(AppealAttachment a) => new()
    {
        AttachmentId = a.AttachmentId,
        ControlNumber = a.ControlNumber,
        AttachmentTypeCode = a.AttachmentTypeCode,
        AttachmentTypeDescription = a.AttachmentTypeDescription,
        TransmissionCode = a.TransmissionCode,
        FileName = a.FileName,
        BlobUrl = a.BlobUrl,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        UploadedAt = a.UploadedAt,
        Description = a.Description,
        Status = a.Status,
        SentDate = a.SentDate,
        AcknowledgmentReceived = a.AcknowledgmentReceived
    };

    private static ClinicalDocument CloneDoc(ClinicalDocument d) => new()
    {
        DocumentId = d.DocumentId,
        DocumentType = d.DocumentType,
        DocumentDate = d.DocumentDate,
        Provider = d.Provider,
        BlobUrl = d.BlobUrl,
        Summary = d.Summary
    };

    private static AppealDecision CloneDecision(AppealDecision d) => new()
    {
        DecisionType = d.DecisionType,
        ApprovedAmount = d.ApprovedAmount,
        DecisionReason = d.DecisionReason,
        ReviewerNotes = d.ReviewerNotes,
        DecisionMaker = d.DecisionMaker,
        DecisionDate = d.DecisionDate
    };

    private static AppealNote CloneNote(AppealNote n) => new()
    {
        NoteId = n.NoteId,
        CreatedAt = n.CreatedAt,
        CreatedBy = n.CreatedBy,
        NoteText = n.NoteText,
        IsInternal = n.IsInternal
    };

    private static AppealEvent CloneEvent(AppealEvent e) => new()
    {
        Id = e.Id,
        PartitionKey = e.PartitionKey,
        TenantId = e.TenantId,
        AppealId = e.AppealId,
        EventId = e.EventId,
        EventType = e.EventType,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        ActorId = e.ActorId,
        CorrelationId = e.CorrelationId,
        OccurredAt = e.OccurredAt,
        Payload = e.Payload
    };
}
