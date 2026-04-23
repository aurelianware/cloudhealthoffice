using AppealsService.Models;

namespace AppealsService.Repositories;

/// <summary>
/// Repository surface for <see cref="Appeal"/>. Mirrors the shape
/// <c>IConsentRepository</c> / <c>IPersonalRepRepository</c> established:
/// tenantId is always the first positional argument on reads; lifecycle
/// methods are verb-named (no generic <c>UpdateAsync</c>, no
/// <c>DeleteAsync</c>). Every mutation bundles the entity update with an
/// <see cref="AppealEvent"/> append — the audit-trail invariant is
/// enforced structurally.
/// </summary>
public interface IAppealRepository
{
    Task<Appeal> CreateAsync(Appeal appeal, AppealEvent genesisEvent, CancellationToken ct = default);

    Task<Appeal?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    Task<Appeal?> GetByAppealNumberAsync(string tenantId, string appealNumber, CancellationToken ct = default);

    Task<IReadOnlyList<Appeal>> GetByClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default);

    /// <summary>
    /// Find the most-recently-submitted non-<see cref="AppealStatus.Closed"/>
    /// appeal for a given claim in a tenant. Returns <c>null</c> when no
    /// open appeal matches.
    /// </summary>
    /// <remarks>
    /// Used by the X12 275 ingress consumer to route an inbound attachment
    /// to the correct existing appeal. The consumer dead-letters when this
    /// returns <c>null</c> rather than fabricating a ghost appeal —
    /// unsolicited 275s without a linkable open appeal are
    /// operator-intervention territory.
    ///
    /// When multiple open appeals exist for the same claim (rare; a first-
    /// level appeal can coexist with a second-level on the same denied
    /// claim), the most-recently-submitted one wins. This matches the
    /// operational intent: a freshly-arrived 275 supports the most active
    /// appeal.
    /// </remarks>
    Task<Appeal?> GetMostRecentAppealByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default);

    Task<IReadOnlyList<Appeal>> SearchAsync(string tenantId, AppealSearchParams p, CancellationToken ct = default);

    Task<AppealsSummary> GetAppealsSummaryAsync(string tenantId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Atomic: persist the new status on <paramref name="appeal"/> AND
    /// append <paramref name="auditEvent"/>. Caller must set
    /// <c>appeal.Status</c> to the new value and populate any lifecycle
    /// fields (ClosedAt, ClosedBy, ClosureReasonCode, etc.) before calling.
    /// Callers MUST have validated the transition via
    /// <c>AppealStateMachine</c>. Conditional on persisted status still
    /// matching <c>auditEvent.FromStatus</c> — a mismatch throws
    /// <see cref="InvalidAppealTransitionException"/>.
    /// </summary>
    Task<Appeal> TransitionStatusAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Race-safe one-shot overdue observer. Conditional on
    /// <c>OverdueAuditEmitted == false</c> AND
    /// <c>Status ∈ {Submitted, InReview, PendingInfo}</c>. Returns the
    /// updated appeal on win (audit event appended). Returns <c>null</c>
    /// on loss — another caller already emitted. Callers that get
    /// <c>null</c> must NOT retry with a different event.
    /// </summary>
    Task<Appeal?> TryTransitionToOverdueAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>Atomic note append + audit event.</summary>
    Task<Appeal> AppendNoteAsync(Appeal appeal, AppealNote note, AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>Atomic attachment append + audit event.</summary>
    Task<Appeal> AppendAttachmentAsync(Appeal appeal, AppealAttachment attachment, AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>Atomic attachment-ack mutation + audit event.</summary>
    Task<Appeal> AcknowledgeAttachmentAsync(
        string tenantId, string appealId, string attachmentId, bool acknowledgmentReceived,
        AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>Atomic reviewer assignment + audit event. Caller sets
    /// <c>appeal.AssignedReviewerId</c> (and optionally appends a note
    /// separately via <see cref="AppendNoteAsync"/> for reassignment
    /// reason) before calling.
    /// </summary>
    Task<Appeal> AssignReviewerAsync(Appeal appeal, AppealEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Returns the note with <paramref name="noteId"/> within the tenant,
    /// together with its parent appeal, or <c>null</c> if not found /
    /// belongs to a different tenant.
    /// </summary>
    Task<AppealNoteLookup?> GetNoteByIdAsync(string tenantId, string noteId, CancellationToken ct = default);

    /// <summary>
    /// Returns the attachment with <paramref name="attachmentId"/> within the
    /// tenant, together with its parent appeal, or <c>null</c> if not found /
    /// belongs to a different tenant.
    /// </summary>
    Task<AppealAttachmentLookup?> GetAttachmentByIdAsync(string tenantId, string attachmentId, CancellationToken ct = default);
}

/// <summary>Search filters — bag of optional query parameters.</summary>
public sealed record AppealSearchParams
{
    public string? MemberId { get; init; }
    public string? ProviderNPI { get; init; }
    public DateTime? SubmittedFrom { get; init; }
    public DateTime? SubmittedTo { get; init; }
    public AppealStatus? Status { get; init; }
    public AppealClosureReasonCode? ClosureReasonCode { get; init; }
    public LineOfBusiness? LineOfBusiness { get; init; }
    public string? AssignedReviewerId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Repository surface for <see cref="AppealEvent"/> (audit trail reads).</summary>
public interface IAppealEventRepository
{
    Task<IReadOnlyList<AppealEvent>> ListByAppealAsync(
        string tenantId,
        string appealId,
        CancellationToken ct = default);
}

/// <summary>Repository-local sink for appending <see cref="AppealEvent"/> rows.
/// Lets the Cosmos and Mongo <see cref="IAppealRepository"/> implementations
/// share a single transition-and-append shape while keeping their own
/// storage choice for audit rows.
/// </summary>
public interface IAppealEventSink
{
    Task AppendAsync(AppealEvent evt, CancellationToken ct = default);
}

/// <summary>Result type for <see cref="IAppealRepository.GetNoteByIdAsync"/>.</summary>
public sealed class AppealNoteLookup
{
    public string AppealId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string NoteId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string NoteText { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Result type for <see cref="IAppealRepository.GetAttachmentByIdAsync"/>.</summary>
public sealed class AppealAttachmentLookup
{
    public string AppealId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AttachmentId { get; set; } = string.Empty;
    public string? ControlNumber { get; set; }
    public string AttachmentTypeCode { get; set; } = string.Empty;
    public string? AttachmentTypeDescription { get; set; }
    public string TransmissionCode { get; set; } = "EL";
    public string? FileName { get; set; }
    public string? BlobUrl { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? Description { get; set; }
    public AttachmentStatus Status { get; set; }
    public DateTime? SentDate { get; set; }
    public bool AcknowledgmentReceived { get; set; }
}
