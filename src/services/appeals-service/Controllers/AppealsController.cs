using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using AppealsService.Middleware;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppealsService.Controllers;

/// <summary>
/// Appeal lifecycle REST surface. Appeals are never hard-deleted; the
/// lifecycle is Draft → Submitted → InReview → PendingInfo → Closed,
/// with an append-only audit trail on every transition and every
/// mutation (note add, attachment add, attachment acknowledge, reviewer
/// assignment).
///
/// Routes stay at <c>/api/appeals/*</c> (unversioned) to preserve the
/// portal contract. Route-versioning across the platform is a separate
/// decision; this modernization PR does NOT bundle it.
///
/// 404-on-cross-tenant: a read for an id that belongs to a different
/// tenant returns 404 (not 403) to avoid tenant enumeration — same posture
/// as consent-service and personal-rep-service.
///
/// 409-on-invalid-transition: state-machine rejections surface as
/// <see cref="ProblemDetails"/> with <c>fromStatus</c>/<c>toStatus</c>
/// extensions. Same shape as consent's <c>ConflictTransition</c>.
///
/// Idempotent same-status requests (e.g. submit when already Submitted)
/// short-circuit at the controller layer — the state machine itself
/// rejects X→X as illegal; the controller handles UX idempotency.
/// </summary>
[ApiController]
[Route("api/appeals")]
[Produces("application/json")]
public class AppealsController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IAppealRepository _appeals;
    private readonly IAppealEventRepository _events;
    private readonly IAppealFieldEncryptor _encryptor;
    private readonly IAppealEventPublisher _publisher;
    private readonly ILogger<AppealsController> _logger;

    public AppealsController(
        IAppealRepository appeals,
        IAppealEventRepository events,
        IAppealFieldEncryptor encryptor,
        IAppealEventPublisher publisher,
        ILogger<AppealsController> logger)
    {
        _appeals = appeals;
        _events = events;
        _encryptor = encryptor;
        _publisher = publisher;
        _logger = logger;
    }

    // ── Create / Read ───────────────────────────────────────────────────

    [HttpPost]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateAppeal([FromBody] CreateAppealRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var actor = User.Identity?.Name ?? "System";
        var now = DateTime.UtcNow;

        var appeal = new Appeal
        {
            TenantId = TenantId,
            AppealNumber = string.IsNullOrEmpty(request.AppealNumber)
                ? $"APL-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
                : request.AppealNumber,
            ClaimId = request.ClaimId,
            ClaimNumber = request.ClaimNumber,
            MemberId = request.MemberId,
            ProviderNPI = request.ProviderNPI,
            ProviderName = request.ProviderName,
            DenialReasonCode = request.DenialReasonCode,
            DeniedAmount = request.DeniedAmount,
            AppealedAmount = request.AppealedAmount,
            AppealType = request.AppealType,
            AppealLevel = request.AppealLevel,
            LineOfBusiness = request.LineOfBusiness,
            Status = AppealStatus.Draft,
            Source = request.Source,
            SubmittedDate = now,
            TargetResponseDate = request.TargetResponseDate ?? now.AddDays(request.IsUrgent ? 30 : 60),
            SubmittedBy = request.SubmittedBy,
            IsUrgent = request.IsUrgent,
            ServiceDate = request.ServiceDate,
            DiagnosisCodes = request.DiagnosisCodes ?? new(),
            ProcedureCodes = request.ProcedureCodes ?? new(),
            AssignedReviewerId = request.AssignedReviewerId,
            CreatedAt = now,
            CreatedBy = actor,

            PatientName = await _encryptor.EncryptAsync(request.PatientName, ct) ?? string.Empty,
            AppealReason = await _encryptor.EncryptAsync(request.AppealReason, ct) ?? string.Empty,
            DenialReason = await _encryptor.EncryptAsync(request.DenialReason, ct)
        };

        var genesis = BuildEvent(appeal, AppealEventType.AppealCreated,
            fromStatus: null, toStatus: AppealStatus.Draft, actor, request.EventId);

        var created = await _appeals.CreateAsync(appeal, genesis, ct);
        await _publisher.PublishCreatedAsync(created, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(created, ct);
        return CreatedAtAction(nameof(GetAppealById), new { id = created.Id }, view);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppealById([FromRoute] string id, CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        appeal = await MaybeObserveOverdueAsync(appeal, ct);

        var view = await DecryptForResponseAsync(appeal, ct);
        return Ok(view);
    }

    [HttpGet("number/{appealNumber}")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppealByNumber([FromRoute] string appealNumber, CancellationToken ct)
    {
        var appeal = await _appeals.GetByAppealNumberAsync(TenantId, appealNumber, ct);
        if (appeal == null) return NotFound();

        appeal = await MaybeObserveOverdueAsync(appeal, ct);

        var view = await DecryptForResponseAsync(appeal, ct);
        return Ok(view);
    }

    [HttpGet("claim/{claimId}")]
    [ProducesResponseType(typeof(AppealListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAppealsByClaimId([FromRoute] string claimId, CancellationToken ct)
    {
        var items = await _appeals.GetByClaimIdAsync(TenantId, claimId, ct);
        var decrypted = await Task.WhenAll(items.Select(a => DecryptForResponseAsync(a, ct)));
        return Ok(new AppealListResponse { Items = decrypted.ToList() });
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(AppealListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAppeals(
        [FromQuery] string? memberId,
        [FromQuery] string? providerNPI,
        [FromQuery] DateTime? submittedFrom,
        [FromQuery] DateTime? submittedTo,
        [FromQuery] AppealStatus? status,
        [FromQuery] AppealClosureReasonCode? closureReasonCode,
        [FromQuery] LineOfBusiness? lineOfBusiness,
        [FromQuery] string? assignedReviewerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var p = new AppealSearchParams
        {
            MemberId = memberId,
            ProviderNPI = providerNPI,
            SubmittedFrom = submittedFrom,
            SubmittedTo = submittedTo,
            Status = status,
            ClosureReasonCode = closureReasonCode,
            LineOfBusiness = lineOfBusiness,
            AssignedReviewerId = assignedReviewerId,
            Page = page,
            PageSize = pageSize
        };

        var items = await _appeals.SearchAsync(TenantId, p, ct);
        var decrypted = await Task.WhenAll(items.Select(a => DecryptForResponseAsync(a, ct)));
        return Ok(new AppealListResponse { Items = decrypted.ToList() });
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(AppealsSummary), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAppealsSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-3);
        var toDate = to ?? DateTime.UtcNow;

        var summary = await _appeals.GetAppealsSummaryAsync(TenantId, fromDate, toDate, ct);
        return Ok(summary);
    }

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(AppealHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory([FromRoute] string id, CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        var events = await _events.ListByAppealAsync(TenantId, id, ct);
        return Ok(new AppealHistoryResponse { Items = events.ToList() });
    }

    // ── Lifecycle transitions ───────────────────────────────────────────

    [HttpPost("{id}/submit")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> Submit([FromRoute] string id, [FromBody] IdempotencyEnvelope? request, CancellationToken ct)
        => RunTransitionAsync(id, AppealStatus.Submitted, request?.EventId, ct);

    [HttpPost("{id}/begin-review")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> BeginReview([FromRoute] string id, [FromBody] IdempotencyEnvelope? request, CancellationToken ct)
        => RunTransitionAsync(id, AppealStatus.InReview, request?.EventId, ct);

    [HttpPost("{id}/resume-review")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> ResumeReview([FromRoute] string id, [FromBody] IdempotencyEnvelope? request, CancellationToken ct)
        => RunTransitionAsync(id, AppealStatus.InReview, request?.EventId, ct);

    [HttpPost("{id}/request-info")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestInfo(
        [FromRoute] string id,
        [FromBody] RequestInfoRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        // Transition to PendingInfo first — this is the state-bearing
        // operation; a failure surfaces as 409. The note append runs after
        // successful transition; a crash window between the two leaves the
        // appeal in PendingInfo without the info-request note, matching
        // consent/personal-rep's accepted audit-trail-annotation posture.
        var transitionResult = await RunTransitionAsync(id, AppealStatus.PendingInfo, request.EventId, ct);
        if (transitionResult is not OkObjectResult okResult || okResult.Value is not Appeal view)
            return transitionResult;

        if (!string.IsNullOrEmpty(request.Description))
        {
            var actor = User.Identity?.Name ?? "System";
            var note = new AppealNote
            {
                CreatedBy = actor,
                NoteText = await _encryptor.EncryptAsync(request.Description, ct) ?? string.Empty,
                IsInternal = false
            };

            // Re-read the appeal in stored (encrypted) form — the note append
            // repository method expects the stored shape, not the decrypted
            // view we just returned to the caller.
            var stored = await _appeals.GetByIdAsync(TenantId, id, ct);
            if (stored != null)
            {
                var noteAudit = BuildEvent(stored, AppealEventType.AppealNoteAdded,
                    fromStatus: null, toStatus: null, actor, eventId: null);
                noteAudit.Payload = new JsonObject
                {
                    ["noteId"] = note.NoteId,
                    ["author"] = note.CreatedBy,
                    ["isInternal"] = note.IsInternal,
                    ["context"] = "request-info"
                };

                var withNote = await _appeals.AppendNoteAsync(stored, note, noteAudit, ct);
                await _publisher.PublishNoteAddedAsync(withNote, note, actor, HttpContext.TraceIdentifier, ct);

                view = await DecryptForResponseAsync(withNote, ct);
            }
        }

        return Ok(view);
    }

    [HttpPost("{id}/withdraw")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(
        [FromRoute] string id,
        [FromBody] WithdrawRequest? request,
        CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        // Idempotent: already closed with reason Withdrawn → 200, no second event.
        if (appeal.Status == AppealStatus.Closed && appeal.ClosureReasonCode == AppealClosureReasonCode.Withdrawn)
        {
            return Ok(await DecryptForResponseAsync(appeal, ct));
        }

        return await CloseInternalAsync(appeal, AppealClosureReasonCode.Withdrawn,
            decision: null, request?.EventId, ct);
    }

    [HttpPost("{id}/close")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Close(
        [FromRoute] string id,
        [FromBody] CloseAppealRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        // Idempotent: same reason already closed → 200, no second event.
        if (appeal.Status == AppealStatus.Closed && appeal.ClosureReasonCode == request.ClosureReasonCode)
        {
            return Ok(await DecryptForResponseAsync(appeal, ct));
        }

        return await CloseInternalAsync(appeal, request.ClosureReasonCode,
            request.Decision, request.EventId, ct);
    }

    // ── Notes / Attachments / Assignment ────────────────────────────────

    [HttpPost("{id}/notes")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(
        [FromRoute] string id,
        [FromBody] AddNoteRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        var actor = User.Identity?.Name ?? "System";
        var note = new AppealNote
        {
            CreatedBy = string.IsNullOrEmpty(request.CreatedBy) ? actor : request.CreatedBy,
            NoteText = await _encryptor.EncryptAsync(request.NoteText, ct) ?? string.Empty,
            IsInternal = request.IsInternal
        };

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealNoteAdded,
            fromStatus: null, toStatus: null, actor, request.EventId);
        auditEvent.Payload = new JsonObject
        {
            ["noteId"] = note.NoteId,
            ["author"] = note.CreatedBy,
            ["isInternal"] = note.IsInternal
        };

        var updated = await _appeals.AppendNoteAsync(appeal, note, auditEvent, ct);
        await _publisher.PublishNoteAddedAsync(updated, note, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(updated, ct);
        return Ok(view);
    }

    [HttpPost("{id}/attachments")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddAttachment(
        [FromRoute] string id,
        [FromBody] AddAttachmentRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        var actor = User.Identity?.Name ?? "System";
        var now = DateTime.UtcNow;

        var attachment = new AppealAttachment
        {
            ControlNumber = string.IsNullOrEmpty(request.ControlNumber)
                ? $"275-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
                : request.ControlNumber,
            AttachmentTypeCode = request.AttachmentTypeCode,
            AttachmentTypeDescription = request.AttachmentTypeDescription,
            TransmissionCode = string.IsNullOrEmpty(request.TransmissionCode) ? "EL" : request.TransmissionCode,
            FileName = request.FileName,
            BlobUrl = request.BlobUrl,
            ContentType = request.ContentType,
            FileSizeBytes = request.FileSizeBytes,
            UploadedAt = now,
            Description = await _encryptor.EncryptAsync(request.Description, ct)
        };

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealAttachmentAdded,
            fromStatus: null, toStatus: null, actor, request.EventId);
        auditEvent.Payload = new JsonObject
        {
            ["attachmentId"] = attachment.AttachmentId,
            ["attachmentTypeCode"] = attachment.AttachmentTypeCode,
            ["transmissionCode"] = attachment.TransmissionCode,
            ["controlNumber"] = attachment.ControlNumber,
            ["uploadedAt"] = attachment.UploadedAt.ToString("o")
        };

        var updated = await _appeals.AppendAttachmentAsync(appeal, attachment, auditEvent, ct);
        await _publisher.PublishAttachmentAddedAsync(updated, attachment, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(updated, ct);
        return Ok(view);
    }

    [HttpPost("{id}/attachments/{attachmentId}/acknowledge")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAttachment(
        [FromRoute] string id,
        [FromRoute] string attachmentId,
        [FromBody] AcknowledgeAttachmentRequest? request,
        CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        var attachment = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
        if (attachment == null) return NotFound();

        var acknowledged = request?.AcknowledgmentReceived ?? true;
        var actor = User.Identity?.Name ?? "System";

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealAttachmentAcknowledged,
            fromStatus: null, toStatus: null, actor, request?.EventId);
        auditEvent.Payload = new JsonObject
        {
            ["attachmentId"] = attachmentId,
            ["acknowledgmentReceived"] = acknowledged
        };

        var updated = await _appeals.AcknowledgeAttachmentAsync(
            TenantId, id, attachmentId, acknowledged, auditEvent, ct);

        var ackAtt = updated.Attachments.First(a => a.AttachmentId == attachmentId);
        await _publisher.PublishAttachmentAcknowledgedAsync(
            updated, ackAtt, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(updated, ct);
        return Ok(view);
    }

    [HttpPost("{id}/assign")]
    [ProducesResponseType(typeof(Appeal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignReviewer(
        [FromRoute] string id,
        [FromBody] AssignReviewerRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        // Idempotent: same reviewer already assigned → 200, no second event.
        if (appeal.AssignedReviewerId == request.AssignedReviewerId)
        {
            return Ok(await DecryptForResponseAsync(appeal, ct));
        }

        var actor = User.Identity?.Name ?? "System";
        var previous = appeal.AssignedReviewerId;

        appeal.AssignedReviewerId = request.AssignedReviewerId;

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealAssigned,
            fromStatus: null, toStatus: null, actor, request.EventId);
        auditEvent.Payload = new JsonObject
        {
            ["assignedReviewerId"] = request.AssignedReviewerId,
            ["previousReviewerId"] = previous
        };

        var updated = await _appeals.AssignReviewerAsync(appeal, auditEvent, ct);

        // Optional reassignment reason becomes a note (atomicity: best effort,
        // same posture as request-info).
        if (!string.IsNullOrEmpty(request.ReassignmentReason))
        {
            var note = new AppealNote
            {
                CreatedBy = actor,
                NoteText = await _encryptor.EncryptAsync(request.ReassignmentReason, ct) ?? string.Empty,
                IsInternal = true
            };
            var noteAudit = BuildEvent(updated, AppealEventType.AppealNoteAdded,
                fromStatus: null, toStatus: null, actor, eventId: null);
            noteAudit.Payload = new JsonObject
            {
                ["noteId"] = note.NoteId,
                ["author"] = note.CreatedBy,
                ["isInternal"] = note.IsInternal,
                ["context"] = "reassignment-reason"
            };
            updated = await _appeals.AppendNoteAsync(updated, note, noteAudit, ct);
            await _publisher.PublishNoteAddedAsync(updated, note, actor, HttpContext.TraceIdentifier, ct);
        }

        await _publisher.PublishAssignedAsync(updated, previous, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(updated, ct);
        return Ok(view);
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private async Task<IActionResult> RunTransitionAsync(
        string id, AppealStatus to, string? eventId, CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal == null) return NotFound();

        appeal = await MaybeObserveOverdueAsync(appeal, ct);

        // Idempotent same-status → 200 no-op. State machine itself rejects
        // X→X as illegal; the idempotency short-circuit lives here.
        if (appeal.Status == to)
        {
            var view = await DecryptForResponseAsync(appeal, ct);
            return Ok(view);
        }

        try
        {
            AppealStateMachine.EnsureAllowed(appeal.Status, to);
        }
        catch (InvalidAppealTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var actor = User.Identity?.Name ?? "System";
        var from = appeal.Status;
        appeal.Status = to;
        appeal.UpdatedAt = DateTime.UtcNow;
        appeal.UpdatedBy = actor;

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealStatusChanged,
            fromStatus: from, toStatus: to, actor, eventId);

        Appeal updated;
        try
        {
            updated = await _appeals.TransitionStatusAsync(appeal, auditEvent, ct);
        }
        catch (InvalidAppealTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        await _publisher.PublishStatusChangedAsync(updated, from, to, actor, HttpContext.TraceIdentifier, ct);

        var result = await DecryptForResponseAsync(updated, ct);
        return Ok(result);
    }

    private async Task<IActionResult> CloseInternalAsync(
        Appeal appeal,
        AppealClosureReasonCode reason,
        AppealDecisionInput? decision,
        string? eventId,
        CancellationToken ct)
    {
        if (!AppealStateMachine.IsAllowed(appeal.Status, AppealStatus.Closed))
        {
            return ConflictTransition(new InvalidAppealTransitionException(appeal.Status, AppealStatus.Closed));
        }

        if (!AppealStateMachine.IsClosureReasonAllowed(appeal.Status, reason))
        {
            var problem = new ProblemDetails
            {
                Status = 409,
                Title = "Invalid appeal closure reason",
                Detail = $"Closure reason {reason} is not allowed from status {appeal.Status}.",
                Type = "https://cloudhealthoffice.com/problems/appeal-closure-reason"
            };
            problem.Extensions["fromStatus"] = appeal.Status.ToString();
            problem.Extensions["closureReasonCode"] = reason.ToString();
            return Conflict(problem);
        }

        var actor = User.Identity?.Name ?? "System";
        var from = appeal.Status;
        var now = DateTime.UtcNow;

        appeal.Status = AppealStatus.Closed;
        appeal.ClosureReasonCode = reason;
        appeal.ClosedAt = now;
        appeal.ClosedBy = actor;
        appeal.UpdatedAt = now;
        appeal.UpdatedBy = actor;

        if (decision != null && reason is AppealClosureReasonCode.Approved
                                 or AppealClosureReasonCode.Denied
                                 or AppealClosureReasonCode.PartialApproval)
        {
            appeal.Decision = new AppealDecision
            {
                DecisionType = decision.DecisionType,
                ApprovedAmount = decision.ApprovedAmount,
                DecisionReason = await _encryptor.EncryptAsync(decision.DecisionReason, ct),
                ReviewerNotes = await _encryptor.EncryptAsync(decision.ReviewerNotes, ct),
                DecisionMaker = string.IsNullOrEmpty(decision.DecisionMaker) ? actor : decision.DecisionMaker,
                DecisionDate = now
            };
            appeal.DecisionDate = now;
        }

        var auditEvent = BuildEvent(appeal, AppealEventType.AppealClosed,
            fromStatus: from, toStatus: AppealStatus.Closed, actor, eventId);
        auditEvent.Payload = new JsonObject
        {
            ["fromStatus"] = from.ToString(),
            ["closureReasonCode"] = reason.ToString(),
            ["decisionType"] = appeal.Decision?.DecisionType.ToString(),
            ["approvedAmount"] = appeal.Decision?.ApprovedAmount
        };

        Appeal updated;
        try
        {
            updated = await _appeals.TransitionStatusAsync(appeal, auditEvent, ct);
        }
        catch (InvalidAppealTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        await _publisher.PublishClosedAsync(updated, from, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(updated, ct);
        return Ok(view);
    }

    private async Task<Appeal> MaybeObserveOverdueAsync(Appeal appeal, CancellationToken ct)
    {
        if (!appeal.IsOverdue || appeal.OverdueAuditEmitted) return appeal;

        var actor = "System";
        var auditEvent = BuildEvent(appeal, AppealEventType.AppealOverdueObserved,
            fromStatus: null, toStatus: null, actor, eventId: null);
        auditEvent.Payload = new JsonObject
        {
            ["currentStatus"] = appeal.Status.ToString(),
            ["targetResponseDate"] = appeal.TargetResponseDate?.ToString("o")
        };

        var persisted = await _appeals.TryTransitionToOverdueAsync(appeal, auditEvent, ct);
        if (persisted != null)
        {
            await _publisher.PublishOverdueObservedAsync(persisted, actor, HttpContext.TraceIdentifier, ct);
            return persisted;
        }

        // Lost the race — another reader already emitted. Keep the local
        // copy's OverdueAuditEmitted flag in sync so we don't retry.
        appeal.OverdueAuditEmitted = true;
        return appeal;
    }

    private static AppealEvent BuildEvent(
        Appeal appeal,
        AppealEventType type,
        AppealStatus? fromStatus,
        AppealStatus? toStatus,
        string actor,
        string? eventId)
    {
        return new AppealEvent
        {
            TenantId = appeal.TenantId,
            AppealId = appeal.Id,
            EventId = string.IsNullOrEmpty(eventId) ? Guid.NewGuid().ToString() : eventId,
            EventType = type,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorId = actor,
            OccurredAt = DateTime.UtcNow
        };
    }

    private async Task<Appeal> DecryptForResponseAsync(Appeal appeal, CancellationToken ct)
    {
        // Shallow copy — leave the stored record untouched; decrypt the
        // outward-facing view only. Avoids aliasing bugs where a caller
        // persists the decrypted copy.
        var view = new Appeal
        {
            TenantId = appeal.TenantId,
            Id = appeal.Id,
            AppealNumber = appeal.AppealNumber,
            ClaimId = appeal.ClaimId,
            ClaimNumber = appeal.ClaimNumber,
            MemberId = appeal.MemberId,
            ProviderNPI = appeal.ProviderNPI,
            ProviderName = appeal.ProviderName,
            DenialReasonCode = appeal.DenialReasonCode,
            DeniedAmount = appeal.DeniedAmount,
            AppealedAmount = appeal.AppealedAmount,
            AppealType = appeal.AppealType,
            AppealLevel = appeal.AppealLevel,
            LineOfBusiness = appeal.LineOfBusiness,
            Status = appeal.Status,
            Source = appeal.Source,
            Attachments = await DecryptAttachmentsAsync(appeal.Attachments, ct),
            ClinicalDocuments = await DecryptClinicalDocsAsync(appeal.ClinicalDocuments, ct),
            Decision = await DecryptDecisionAsync(appeal.Decision, ct),
            SubmittedDate = appeal.SubmittedDate,
            ReceivedDate = appeal.ReceivedDate,
            TargetResponseDate = appeal.TargetResponseDate,
            DecisionDate = appeal.DecisionDate,
            SubmittedBy = appeal.SubmittedBy,
            Notes = await DecryptNotesAsync(appeal.Notes, ct),
            AttachmentControlNumbers = appeal.AttachmentControlNumbers,
            IsUrgent = appeal.IsUrgent,
            ServiceDate = appeal.ServiceDate,
            DiagnosisCodes = appeal.DiagnosisCodes,
            ProcedureCodes = appeal.ProcedureCodes,
            AssignedReviewerId = appeal.AssignedReviewerId,
            CreatedAt = appeal.CreatedAt,
            CreatedBy = appeal.CreatedBy,
            UpdatedAt = appeal.UpdatedAt,
            UpdatedBy = appeal.UpdatedBy,
            ClosedAt = appeal.ClosedAt,
            ClosedBy = appeal.ClosedBy,
            ClosureReasonCode = appeal.ClosureReasonCode,
            OverdueAuditEmitted = appeal.OverdueAuditEmitted,

            PatientName = await _encryptor.DecryptAsync(appeal.PatientName, ct) ?? string.Empty,
            AppealReason = await _encryptor.DecryptAsync(appeal.AppealReason, ct) ?? string.Empty,
            DenialReason = await _encryptor.DecryptAsync(appeal.DenialReason, ct)
        };
        return view;
    }

    private async Task<List<AppealNote>> DecryptNotesAsync(List<AppealNote> notes, CancellationToken ct)
    {
        var results = new List<AppealNote>(notes.Count);
        foreach (var n in notes)
        {
            results.Add(new AppealNote
            {
                NoteId = n.NoteId,
                CreatedAt = n.CreatedAt,
                CreatedBy = n.CreatedBy,
                NoteText = await _encryptor.DecryptAsync(n.NoteText, ct) ?? string.Empty,
                IsInternal = n.IsInternal
            });
        }
        return results;
    }

    private async Task<List<AppealAttachment>> DecryptAttachmentsAsync(List<AppealAttachment> attachments, CancellationToken ct)
    {
        var results = new List<AppealAttachment>(attachments.Count);
        foreach (var a in attachments)
        {
            results.Add(new AppealAttachment
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
                Description = await _encryptor.DecryptAsync(a.Description, ct),
                Status = a.Status,
                SentDate = a.SentDate,
                AcknowledgmentReceived = a.AcknowledgmentReceived
            });
        }
        return results;
    }

    private async Task<List<ClinicalDocument>> DecryptClinicalDocsAsync(List<ClinicalDocument> docs, CancellationToken ct)
    {
        var results = new List<ClinicalDocument>(docs.Count);
        foreach (var d in docs)
        {
            results.Add(new ClinicalDocument
            {
                DocumentId = d.DocumentId,
                DocumentType = d.DocumentType,
                DocumentDate = d.DocumentDate,
                Provider = d.Provider,
                BlobUrl = d.BlobUrl,
                Summary = await _encryptor.DecryptAsync(d.Summary, ct)
            });
        }
        return results;
    }

    private async Task<AppealDecision?> DecryptDecisionAsync(AppealDecision? decision, CancellationToken ct)
    {
        if (decision == null) return null;
        return new AppealDecision
        {
            DecisionType = decision.DecisionType,
            ApprovedAmount = decision.ApprovedAmount,
            DecisionReason = await _encryptor.DecryptAsync(decision.DecisionReason, ct),
            ReviewerNotes = await _encryptor.DecryptAsync(decision.ReviewerNotes, ct),
            DecisionMaker = decision.DecisionMaker,
            DecisionDate = decision.DecisionDate
        };
    }

    private IActionResult ConflictTransition(InvalidAppealTransitionException ex)
    {
        var problem = new ProblemDetails
        {
            Status = 409,
            Title = "Invalid appeal transition",
            Detail = ex.Message,
            Type = "https://cloudhealthoffice.com/problems/appeal-transition"
        };
        problem.Extensions["fromStatus"] = ex.FromStatus.ToString();
        problem.Extensions["toStatus"] = ex.ToStatus.ToString();
        return Conflict(problem);
    }

    // ── Per-note and per-attachment lookup (used by fhir-service) ───────

    /// <summary>GET /api/appeals/notes/{noteId} — fetch a single note across any appeal for the tenant.</summary>
    [HttpGet("notes/{noteId}")]
    [ProducesResponseType(typeof(AppealNoteLookup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNoteById([FromRoute] string noteId, CancellationToken ct)
    {
        var lookup = await _appeals.GetNoteByIdAsync(TenantId, noteId, ct);
        if (lookup is null) return NotFound();

        // Decrypt note text before returning (NoteText is encrypted at rest)
        lookup.NoteText = await _encryptor.DecryptAsync(lookup.NoteText, ct) ?? string.Empty;
        return Ok(lookup);
    }

    /// <summary>GET /api/appeals/attachments/{attachmentId} — fetch a single attachment across any appeal for the tenant.</summary>
    [HttpGet("attachments/{attachmentId}")]
    [ProducesResponseType(typeof(AppealAttachmentLookup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAttachmentById([FromRoute] string attachmentId, CancellationToken ct)
    {
        var lookup = await _appeals.GetAttachmentByIdAsync(TenantId, attachmentId, ct);
        if (lookup is null) return NotFound();

        // Decrypt description before returning (Description is encrypted at rest)
        lookup.Description = await _encryptor.DecryptAsync(lookup.Description, ct);
        return Ok(lookup);
    }
}

// ── Request / response DTOs ─────────────────────────────────────────────

public class CreateAppealRequest
{
    [StringLength(50)]
    public string? AppealNumber { get; set; }

    [Required] [StringLength(50)]
    public string ClaimId { get; set; } = string.Empty;

    [Required] [StringLength(50)]
    public string ClaimNumber { get; set; } = string.Empty;

    [Required] [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [Required] [StringLength(200)]
    public string PatientName { get; set; } = string.Empty;

    [Required] [StringLength(10)]
    public string ProviderNPI { get; set; } = string.Empty;

    [StringLength(300)]
    public string? ProviderName { get; set; }

    [StringLength(5)]
    public string? DenialReasonCode { get; set; }

    [StringLength(4000)]
    public string? DenialReason { get; set; }

    public decimal DeniedAmount { get; set; }
    public decimal AppealedAmount { get; set; }

    [Required] public AppealType AppealType { get; set; } = AppealType.Reconsideration;

    [Required] public AppealLevel AppealLevel { get; set; } = AppealLevel.FirstLevel;

    [Required] public LineOfBusiness LineOfBusiness { get; set; }

    [Required] [StringLength(8000)]
    public string AppealReason { get; set; } = string.Empty;

    public AppealSource Source { get; set; } = AppealSource.ProviderPortal;

    public DateTime? TargetResponseDate { get; set; }

    [StringLength(100)]
    public string? SubmittedBy { get; set; }

    public bool IsUrgent { get; set; }
    public DateTime? ServiceDate { get; set; }

    public List<string>? DiagnosisCodes { get; set; }
    public List<string>? ProcedureCodes { get; set; }

    [StringLength(200)]
    public string? AssignedReviewerId { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class CloseAppealRequest
{
    [Required]
    public AppealClosureReasonCode ClosureReasonCode { get; set; }

    public AppealDecisionInput? Decision { get; set; }

    public string? EventId { get; set; }
}

public class AppealDecisionInput
{
    [Required] public AppealDecisionType DecisionType { get; set; }

    public decimal? ApprovedAmount { get; set; }

    [StringLength(8000)]
    public string? DecisionReason { get; set; }

    [StringLength(8000)]
    public string? ReviewerNotes { get; set; }

    [StringLength(100)]
    public string? DecisionMaker { get; set; }
}

public class WithdrawRequest
{
    public string? EventId { get; set; }
}

public class RequestInfoRequest
{
    [Required] [StringLength(4000)]
    public string Description { get; set; } = string.Empty;

    public string? EventId { get; set; }
}

public class IdempotencyEnvelope
{
    public string? EventId { get; set; }
}

public class AddNoteRequest
{
    [Required] [StringLength(8000)]
    public string NoteText { get; set; } = string.Empty;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    public bool IsInternal { get; set; } = true;

    public string? EventId { get; set; }
}

public class AddAttachmentRequest
{
    [Required] [StringLength(2)]
    public string AttachmentTypeCode { get; set; } = string.Empty;

    public string? AttachmentTypeDescription { get; set; }

    [StringLength(2)]
    public string? TransmissionCode { get; set; }

    [StringLength(50)]
    public string? ControlNumber { get; set; }

    [StringLength(300)]
    public string? FileName { get; set; }

    public string? BlobUrl { get; set; }

    [StringLength(50)]
    public string? ContentType { get; set; }

    public long? FileSizeBytes { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    public string? EventId { get; set; }
}

public class AcknowledgeAttachmentRequest
{
    public bool AcknowledgmentReceived { get; set; } = true;
    public string? EventId { get; set; }
}

public class AssignReviewerRequest
{
    [Required] [StringLength(200)]
    public string AssignedReviewerId { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? ReassignmentReason { get; set; }

    public string? EventId { get; set; }
}

public class AppealListResponse
{
    public List<Appeal> Items { get; set; } = new();
}

public class AppealHistoryResponse
{
    public List<AppealEvent> Items { get; set; } = new();
}
