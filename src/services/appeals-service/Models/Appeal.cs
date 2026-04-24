using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

// TODO(appeals-followup-pr3): FHIR Task / Communication / DocumentReference /
// ClaimResponse rendering belongs in fhir-service via the existing
// IFhirDataAdapter pattern — not here. appeals-service owns the bespoke
// domain model.

namespace AppealsService.Models;

/// <summary>
/// A claim appeal request with 275 attachment support. Appeals are submitted
/// when claims are denied and require additional documentation. Appeals are
/// never hard-deleted; lifecycle transitions (Draft → Submitted → InReview →
/// PendingInfo → Closed) are captured through
/// <see cref="Repositories.IAppealRepository.TransitionStatusAsync"/> with a
/// matching <see cref="AppealEvent"/> appended atomically for audit.
///
/// PHI-adjacent free-text fields (<see cref="PatientName"/>,
/// <see cref="AppealReason"/>, <see cref="DenialReason"/>, note text, decision
/// reasons, reviewer notes, clinical document summaries, attachment
/// descriptions) are stored as ciphertext; encryption is applied by the
/// controller layer before persistence, decryption on read-back. The fields
/// are always accessed through the entity in ciphertext form — repositories
/// do not decrypt.
/// </summary>
[BsonIgnoreExtraElements]
public class Appeal
{
    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Stable appeal id. Cosmos document id and Mongo `_id`.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>User-facing tracking number. Unique within tenant.</summary>
    [Required]
    [StringLength(50)]
    public string AppealNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ClaimId { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string ClaimNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Patient name. Encrypted at rest via
    /// <see cref="Services.IAppealFieldEncryptor"/>.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string PatientName { get; set; } = string.Empty;

    [Required]
    [StringLength(10)]
    public string ProviderNPI { get; set; } = string.Empty;

    [StringLength(300)]
    public string? ProviderName { get; set; }

    /// <summary>Original denial reason code from the claim. Not PHI.</summary>
    [StringLength(5)]
    public string? DenialReasonCode { get; set; }

    /// <summary>
    /// Original denial reason description from the claim. Free text —
    /// encrypted at rest.
    /// </summary>
    [StringLength(4000)]
    public string? DenialReason { get; set; }

    public decimal DeniedAmount { get; set; }

    public decimal AppealedAmount { get; set; }

    [Required]
    public AppealType AppealType { get; set; } = AppealType.Reconsideration;

    [Required]
    public AppealLevel AppealLevel { get; set; } = AppealLevel.FirstLevel;

    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    [Required]
    public AppealStatus Status { get; set; } = AppealStatus.Draft;

    /// <summary>
    /// The provider's argument for the appeal. Free text — encrypted at
    /// rest.
    /// </summary>
    [Required]
    [StringLength(8000)]
    public string AppealReason { get; set; } = string.Empty;

    /// <summary>
    /// Ingress channel. Defaults to <see cref="AppealSource.ProviderPortal"/>.
    /// Future ingress channels (Availity 275, CSR transcription, internal
    /// retro review, external review) come in follow-up PRs.
    /// </summary>
    public AppealSource Source { get; set; } = AppealSource.ProviderPortal;

    public List<AppealAttachment> Attachments { get; set; } = new();

    public List<ClinicalDocument> ClinicalDocuments { get; set; } = new();

    /// <summary>
    /// Decision outcome. Populated when <see cref="Status"/> transitions to
    /// <see cref="AppealStatus.Closed"/> with a decision-bearing
    /// <see cref="ClosureReasonCode"/>.
    /// </summary>
    public AppealDecision? Decision { get; set; }

    // ── Lifecycle timestamps ────────────────────────────────────────────

    [Required]
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    public DateTime? ReceivedDate { get; set; }

    /// <summary>Regulatory deadline. Drives the read-time overdue projection.</summary>
    public DateTime? TargetResponseDate { get; set; }

    public DateTime? DecisionDate { get; set; }

    /// <summary>
    /// User who submitted the appeal.
    /// TODO(appeals-followup-personal-rep-integration): When filed on behalf
    /// of a member by a Personal Representative, the SubmittedBy field
    /// should resolve to a personal-rep-service reference. Not this PR.
    /// TODO(appeals-followup-consent-integration): When filed on behalf of
    /// a member who granted §164.508 authorization, the chain should be
    /// recorded via consent-service. Not this PR.
    /// </summary>
    [StringLength(100)]
    public string? SubmittedBy { get; set; }

    public List<AppealNote> Notes { get; set; } = new();

    /// <summary>275 attachment transaction control numbers.</summary>
    public List<string> AttachmentControlNumbers { get; set; } = new();

    public bool IsUrgent { get; set; }

    public DateTime? ServiceDate { get; set; }

    /// <summary>
    /// Diagnosis codes from the original claim. Plain-stored for
    /// queryability — matches the claims-service posture. Legal review to
    /// confirm for the deploying tenant before production use.
    /// </summary>
    public List<string> DiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Procedure codes from the original claim. Plain-stored for
    /// queryability — matches the claims-service posture. Legal review to
    /// confirm for the deploying tenant before production use.
    /// </summary>
    public List<string> ProcedureCodes { get; set; } = new();

    /// <summary>
    /// Assigned reviewer id (plain-stored, not encrypted). Mutated only via
    /// the <c>POST /{id}/assign</c> endpoint, which writes an
    /// <c>AppealAssigned</c> audit event.
    /// TODO(appeals-followup-work-queue): Auto-assignment rules and
    /// workload balancing live in a future work-queue service.
    /// </summary>
    [StringLength(200)]
    public string? AssignedReviewerId { get; set; }

    // ── Audit timestamps ────────────────────────────────────────────────

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }

    public DateTime? ClosedAt { get; set; }

    [StringLength(200)]
    public string? ClosedBy { get; set; }

    /// <summary>
    /// Controlled reason code for closure. Safe to include in event payload
    /// (enum-valued). Populated when <see cref="Status"/> is
    /// <see cref="AppealStatus.Closed"/>. See
    /// <see cref="AppealClosureReasonCode"/>.
    /// </summary>
    public AppealClosureReasonCode? ClosureReasonCode { get; set; }

    /// <summary>
    /// One-shot marker for the read-time overdue observer. Set to
    /// <c>true</c> by
    /// <see cref="Repositories.IAppealRepository.TryTransitionToOverdueAsync"/>
    /// when the first read past <see cref="TargetResponseDate"/> emits an
    /// <c>AppealOverdueObserved</c> audit event. Prevents duplicate overdue
    /// events for the same appeal.
    /// </summary>
    public bool OverdueAuditEmitted { get; set; }

    /// <summary>
    /// Projects the persisted status into the status the caller observes —
    /// the raw <see cref="Status"/> today; overdue is NOT a status (it's a
    /// read-time projection on <see cref="IsOverdue"/>). This method is
    /// kept for symmetry with consent-service/personal-rep-service and will
    /// gain responsibilities if a future status (e.g. a review-pending
    /// state) joins the lifecycle.
    /// </summary>
    public AppealStatus ObservedStatus(DateTime? asOf = null) => Status;

    /// <summary>
    /// Read-time projection: <c>true</c> when the appeal is in a
    /// non-terminal status and <see cref="TargetResponseDate"/> has passed.
    /// </summary>
    [BsonIgnore]
    public bool IsOverdue =>
        TargetResponseDate.HasValue &&
        TargetResponseDate.Value <= DateTime.UtcNow &&
        Status != AppealStatus.Draft &&
        Status != AppealStatus.Closed;
}

/// <summary>
/// 275 attachment submission record. The <see cref="BlobUrl"/> is a
/// member-document-service reference — appeals-service does NOT store blob
/// bytes.
/// </summary>
[BsonIgnoreExtraElements]
public class AppealAttachment
{
    [Required]
    public string AttachmentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>275 transaction control number (PWK06 equivalent).</summary>
    [StringLength(50)]
    public string? ControlNumber { get; set; }

    /// <summary>Attachment type code (275: PWK01).</summary>
    [Required]
    [StringLength(2)]
    public string AttachmentTypeCode { get; set; } = string.Empty;

    public string? AttachmentTypeDescription { get; set; }

    /// <summary>
    /// Transmission code (275: PWK02). AA=Available, BM=By Mail,
    /// EL=Electronically, etc.
    /// </summary>
    [Required]
    [StringLength(2)]
    public string TransmissionCode { get; set; } = "EL";

    [StringLength(300)]
    public string? FileName { get; set; }

    /// <summary>
    /// Reference into member-document-service where the document bytes live.
    /// appeals-service does NOT store blobs.
    /// </summary>
    public string? BlobUrl { get; set; }

    [StringLength(50)]
    public string? ContentType { get; set; }

    public long? FileSizeBytes { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Free-text description. May contain PHI — encrypted at rest.
    /// </summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    public AttachmentStatus Status { get; set; } = AttachmentStatus.Pending;

    public DateTime? SentDate { get; set; }

    public bool AcknowledgmentReceived { get; set; }
}

/// <summary>Clinical documentation reference.</summary>
[BsonIgnoreExtraElements]
public class ClinicalDocument
{
    public string DocumentId { get; set; } = Guid.NewGuid().ToString();
    public string DocumentType { get; set; } = string.Empty;
    public string? DocumentDate { get; set; }
    public string? Provider { get; set; }
    public string? BlobUrl { get; set; }

    /// <summary>Free-text clinical summary. PHI — encrypted at rest.</summary>
    [StringLength(4000)]
    public string? Summary { get; set; }
}

/// <summary>Appeal decision outcome.</summary>
[BsonIgnoreExtraElements]
public class AppealDecision
{
    [Required]
    public AppealDecisionType DecisionType { get; set; }

    public decimal? ApprovedAmount { get; set; }

    /// <summary>Decision rationale. Free text — encrypted at rest.</summary>
    [StringLength(8000)]
    public string? DecisionReason { get; set; }

    [StringLength(100)]
    public string? DecisionMaker { get; set; }

    public DateTime DecisionDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Additional reviewer notes. Free text — encrypted at rest.
    /// </summary>
    [StringLength(8000)]
    public string? ReviewerNotes { get; set; }
}

/// <summary>Appeal note / comment.</summary>
[BsonIgnoreExtraElements]
public class AppealNote
{
    public string NoteId { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Note body. Free text — encrypted at rest.</summary>
    [StringLength(8000)]
    public string NoteText { get; set; } = string.Empty;

    public bool IsInternal { get; set; } = true;
}

public enum AppealType
{
    Reconsideration = 1,
    PeerReview = 2,
    ExternalReview = 3,
    Grievance = 4
}

public enum AppealLevel
{
    FirstLevel = 1,
    SecondLevel = 2,
    ExternalReview = 3
}

/// <summary>
/// Appeal lifecycle state. See <c>Services.AppealStateMachine</c> for the
/// allowed transitions. The four historical terminal values (Approved,
/// Denied, PartialApproval, Withdrawn) consolidate into
/// <see cref="Closed"/> plus an <see cref="AppealClosureReasonCode"/>
/// discriminator — mirrors the pattern
/// <c>PersonalRepInactivationReasonCode</c> established.
/// </summary>
public enum AppealStatus
{
    Draft = 1,
    Submitted = 2,
    InReview = 3,
    PendingInfo = 4,
    Closed = 5
}

public enum AppealDecisionType
{
    Approved = 1,
    Denied = 2,
    PartialApproval = 3
}

public enum AttachmentStatus
{
    Pending = 1,
    Sent = 2,
    Acknowledged = 3,
    Rejected = 4,
    Error = 5
}

public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Marketplace = 4
}

/// <summary>
/// Controlled closure reasons. Safe to include in event payloads (no PHI).
/// Unlike consent-service which models Expired as a distinct terminal
/// status, appeals collapses all termination reasons into
/// <see cref="AppealStatus.Closed"/> with a discriminator here — see
/// <c>Services.AppealStateMachine</c> remarks for rationale.
/// </summary>
public enum AppealClosureReasonCode
{
    Approved = 1,
    Denied = 2,
    PartialApproval = 3,
    Withdrawn = 4,
    Expired = 5,
    AdminError = 6,
    Other = 99
}

/// <summary>
/// Ingress channel for an appeal. Zero-cost foresight for PR 4's 275
/// consumer and future ingress work — default today is
/// <see cref="ProviderPortal"/>; no behavior branches on this yet.
/// </summary>
public enum AppealSource
{
    ProviderPortal = 1,
    Availity275 = 2,
    CsrTranscription = 3,
    InternalRetroReview = 4,
    ExternalReview = 5
}

/// <summary>
/// Appeals summary statistics. Wire shape is stable — the four portal-
/// observed buckets (<see cref="OpenAppeals"/>, <see cref="UrgentExpedited"/>,
/// <see cref="DueThisWeek"/>, <see cref="OverturnedRate"/>) are preserved
/// across the status-enum consolidation by recomputing over
/// <c>Status + ClosureReasonCode</c>.
/// </summary>
public class AppealsSummary
{
    public int TotalAppeals { get; set; }
    public int InReview { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    public int PartialApprovals { get; set; }
    public int Withdrawn { get; set; }
    public decimal TotalAppealedAmount { get; set; }
    public decimal TotalApprovedAmount { get; set; }
    public double AverageDecisionTimeDays { get; set; }
    public double ApprovalRate { get; set; }

    // ── Portal-observed wire-shape buckets ──────────────────────────────
    /// <summary>
    /// Appeals in a non-terminal status (Submitted, InReview, PendingInfo).
    /// Draft is excluded — a draft is not yet an open appeal from the
    /// payer's perspective.
    /// </summary>
    public int OpenAppeals { get; set; }

    /// <summary>Open appeals flagged urgent.</summary>
    public int UrgentExpedited { get; set; }

    /// <summary>
    /// Open appeals with <c>TargetResponseDate</c> within the next 7 days
    /// (including already-overdue).
    /// </summary>
    public int DueThisWeek { get; set; }

    /// <summary>
    /// Percentage of Closed appeals with
    /// <c>ClosureReasonCode ∈ {Approved, PartialApproval}</c> — the rate at
    /// which original denials were overturned on appeal. 0–100.
    /// </summary>
    public double OverturnedRate { get; set; }

    public Dictionary<AppealStatus, int> AppealsByStatus { get; set; } = new();
    public Dictionary<AppealLevel, int> AppealsByLevel { get; set; } = new();
}

/// <summary>
/// Thrown when a caller attempts an appeal lifecycle transition that is not
/// allowed by <c>Services.AppealStateMachine</c>. Distinct from a generic
/// <see cref="InvalidOperationException"/> so the controller layer can map
/// it to a 409 Conflict with <see cref="FromStatus"/>/<see cref="ToStatus"/>
/// in ProblemDetails rather than a 500.
/// </summary>
public sealed class InvalidAppealTransitionException : InvalidOperationException
{
    public AppealStatus FromStatus { get; }
    public AppealStatus ToStatus { get; }

    public InvalidAppealTransitionException(AppealStatus from, AppealStatus to)
        : base($"Appeal transition {from} -> {to} is not allowed.")
    {
        FromStatus = from;
        ToStatus = to;
    }
}
