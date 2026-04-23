using System.Text.Json.Serialization;

namespace CloudHealthOffice.Appeals.Contracts;

/// <summary>
/// Cross-service contract for an appeal record. Mirrors
/// <c>AppealsService.Models.Appeal</c> (the domain aggregate in
/// appeals-service) as a parallel flat DTO with no Mongo / validation /
/// encryption concerns. Property names, types, and nullability are
/// structurally paired — <c>AppealDtoDriftTests</c> enforces the parity.
///
/// Sensitive free-text fields that the domain stores encrypted at rest
/// (<see cref="PatientName"/>, <see cref="AppealReason"/>,
/// <see cref="DenialReason"/>, note text, decision rationale, reviewer
/// notes, clinical document summaries, attachment descriptions) are
/// exchanged in PLAINTEXT over the wire — the caller (fhir-service) is
/// responsible for calling the authenticated appeals-service read API
/// which performs decryption before sending.
/// </summary>
public sealed class AppealDto
{
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string AppealNumber { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;

    public string PatientName { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string? ProviderName { get; set; }

    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }

    public decimal DeniedAmount { get; set; }
    public decimal AppealedAmount { get; set; }

    public AppealType AppealType { get; set; } = AppealType.Reconsideration;
    public AppealLevel AppealLevel { get; set; } = AppealLevel.FirstLevel;
    public LineOfBusiness LineOfBusiness { get; set; }
    public AppealStatus Status { get; set; } = AppealStatus.Draft;

    public string AppealReason { get; set; } = string.Empty;

    public AppealSource Source { get; set; } = AppealSource.ProviderPortal;

    public List<AppealAttachmentDto> Attachments { get; set; } = new();
    public List<ClinicalDocumentDto> ClinicalDocuments { get; set; } = new();
    public AppealDecisionDto? Decision { get; set; }

    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ReceivedDate { get; set; }
    public DateTime? TargetResponseDate { get; set; }
    public DateTime? DecisionDate { get; set; }

    public string? SubmittedBy { get; set; }

    public List<AppealNoteDto> Notes { get; set; } = new();
    public List<string> AttachmentControlNumbers { get; set; } = new();

    public bool IsUrgent { get; set; }
    public DateTime? ServiceDate { get; set; }

    public List<string> DiagnosisCodes { get; set; } = new();
    public List<string> ProcedureCodes { get; set; } = new();

    public string? AssignedReviewerId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public AppealClosureReasonCode? ClosureReasonCode { get; set; }

    public bool OverdueAuditEmitted { get; set; }
}

public sealed class AppealAttachmentDto
{
    public string AttachmentId { get; set; } = Guid.NewGuid().ToString();
    public string? ControlNumber { get; set; }
    public string AttachmentTypeCode { get; set; } = string.Empty;
    public string? AttachmentTypeDescription { get; set; }
    public string TransmissionCode { get; set; } = "EL";
    public string? FileName { get; set; }
    public string? BlobUrl { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    public AttachmentStatus Status { get; set; } = AttachmentStatus.Pending;
    public DateTime? SentDate { get; set; }
    public bool AcknowledgmentReceived { get; set; }
}

public sealed class ClinicalDocumentDto
{
    public string DocumentId { get; set; } = Guid.NewGuid().ToString();
    public string DocumentType { get; set; } = string.Empty;
    public string? DocumentDate { get; set; }
    public string? Provider { get; set; }
    public string? BlobUrl { get; set; }
    public string? Summary { get; set; }
}

public sealed class AppealDecisionDto
{
    public AppealDecisionType DecisionType { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public string? DecisionReason { get; set; }
    public string? DecisionMaker { get; set; }
    public DateTime DecisionDate { get; set; } = DateTime.UtcNow;
    public string? ReviewerNotes { get; set; }
}

public sealed class AppealNoteDto
{
    public string NoteId { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public string NoteText { get; set; } = string.Empty;
    public bool IsInternal { get; set; } = true;
}
