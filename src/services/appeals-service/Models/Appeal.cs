using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace AppealsService.Models;

/// <summary>
/// Represents a claim appeal request with 275 attachment support
/// Appeals are submitted when claims are denied and require additional documentation
/// </summary>
public class Appeal
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique appeal identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Appeal tracking number (user-facing)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string AppealNumber { get; set; } = string.Empty;

    /// <summary>
    /// Original claim ID being appealed
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Original claim number
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ClaimNumber { get; set; } = string.Empty;

    /// <summary>
    /// Member ID
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Patient name
    /// </summary>
    [Required]
    [StringLength(200)]
    public string PatientName { get; set; } = string.Empty;

    /// <summary>
    /// Provider NPI submitting appeal
    /// </summary>
    [Required]
    [StringLength(10)]
    public string ProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Provider name
    /// </summary>
    [StringLength(300)]
    public string? ProviderName { get; set; }

    /// <summary>
    /// Original denial reason code
    /// </summary>
    [StringLength(5)]
    public string? DenialReasonCode { get; set; }

    /// <summary>
    /// Original denial reason description
    /// </summary>
    public string? DenialReason { get; set; }

    /// <summary>
    /// Original denied amount
    /// </summary>
    public decimal DeniedAmount { get; set; }

    /// <summary>
    /// Amount being appealed
    /// </summary>
    public decimal AppealedAmount { get; set; }

    /// <summary>
    /// Appeal type
    /// </summary>
    [Required]
    public AppealType AppealType { get; set; } = AppealType.Reconsideration;

    /// <summary>
    /// Appeal level (first, second, external review)
    /// </summary>
    [Required]
    public AppealLevel AppealLevel { get; set; } = AppealLevel.FirstLevel;

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Appeal status
    /// </summary>
    [Required]
    public AppealStatus Status { get; set; } = AppealStatus.Submitted;

    /// <summary>
    /// Reason for appeal (provider's argument)
    /// </summary>
    [Required]
    public string AppealReason { get; set; } = string.Empty;

    /// <summary>
    /// Supporting documentation/attachments (275 transaction references)
    /// </summary>
    public List<AppealAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// Clinical documentation references
    /// </summary>
    public List<ClinicalDocument> ClinicalDocuments { get; set; } = new();

    /// <summary>
    /// Appeal decision outcome
    /// </summary>
    public AppealDecision? Decision { get; set; }

    /// <summary>
    /// Date appeal submitted
    /// </summary>
    [Required]
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date appeal was received by payer
    /// </summary>
    public DateTime? ReceivedDate { get; set; }

    /// <summary>
    /// Target response date (regulatory deadline)
    /// </summary>
    public DateTime? TargetResponseDate { get; set; }

    /// <summary>
    /// Date decision was made
    /// </summary>
    public DateTime? DecisionDate { get; set; }

    /// <summary>
    /// User who submitted the appeal
    /// </summary>
    [StringLength(100)]
    public string? SubmittedBy { get; set; }

    /// <summary>
    /// Internal notes
    /// </summary>
    public List<AppealNote> Notes { get; set; } = new();

    /// <summary>
    /// 275 attachment transaction control numbers
    /// </summary>
    public List<string> AttachmentControlNumbers { get; set; } = new();

    /// <summary>
    /// Priority flag
    /// </summary>
    public bool IsUrgent { get; set; } = false;

    /// <summary>
    /// Service date from original claim
    /// </summary>
    public DateTime? ServiceDate { get; set; }

    /// <summary>
    /// Diagnosis codes from original claim
    /// </summary>
    public List<string> DiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Procedure codes from original claim
    /// </summary>
    public List<string> ProcedureCodes { get; set; } = new();
}

/// <summary>
/// 275 attachment submission record
/// 275: Attachment/additional documentation transaction
/// </summary>
public class AppealAttachment
{
    /// <summary>
    /// Attachment ID
    /// </summary>
    [Required]
    public string AttachmentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 275 transaction control number
    /// </summary>
    [StringLength(50)]
    public string? ControlNumber { get; set; }

    /// <summary>
    /// Attachment type code (275: PWK01)
    /// </summary>
    [Required]
    [StringLength(2)]
    public string AttachmentTypeCode { get; set; } = string.Empty;

    /// <summary>
    /// Attachment type description
    /// </summary>
    public string? AttachmentTypeDescription { get; set; }

    /// <summary>
    /// Transmission code (275: PWK02) - AA=Available, BM=By Mail, EL=Electronically, etc.
    /// </summary>
    [Required]
    [StringLength(2)]
    public string TransmissionCode { get; set; } = "EL";

    /// <summary>
    /// File name or document reference
    /// </summary>
    [StringLength(300)]
    public string? FileName { get; set; }

    /// <summary>
    /// Blob storage URL for uploaded document
    /// </summary>
    public string? BlobUrl { get; set; }

    /// <summary>
    /// Content type (PDF, TIFF, etc.)
    /// </summary>
    [StringLength(50)]
    public string? ContentType { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Upload timestamp
    /// </summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Description/notes about this attachment
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 275 submission status
    /// </summary>
    public AttachmentStatus Status { get; set; } = AttachmentStatus.Pending;

    /// <summary>
    /// Date attachment was sent via 275 transaction
    /// </summary>
    public DateTime? SentDate { get; set; }

    /// <summary>
    /// Acknowledgment received
    /// </summary>
    public bool AcknowledgmentReceived { get; set; } = false;
}

/// <summary>
/// Clinical documentation reference
/// </summary>
public class ClinicalDocument
{
    public string DocumentId { get; set; } = Guid.NewGuid().ToString();
    public string DocumentType { get; set; } = string.Empty; // Progress Note, Operative Report, Lab Results, etc.
    public string? DocumentDate { get; set; }
    public string? Provider { get; set; }
    public string? BlobUrl { get; set; }
    public string? Summary { get; set; }
}

/// <summary>
/// Appeal decision outcome
/// </summary>
public class AppealDecision
{
    /// <summary>
    /// Decision code (Approved, Denied, Partial)
    /// </summary>
    [Required]
    public AppealDecisionType DecisionType { get; set; }

    /// <summary>
    /// Approved amount (if approved/partial)
    /// </summary>
    public decimal? ApprovedAmount { get; set; }

    /// <summary>
    /// Decision reason/rationale
    /// </summary>
    public string? DecisionReason { get; set; }

    /// <summary>
    /// Decision maker (reviewer name/ID)
    /// </summary>
    [StringLength(100)]
    public string? DecisionMaker { get; set; }

    /// <summary>
    /// Date decision was made
    /// </summary>
    public DateTime DecisionDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Additional reviewer notes
    /// </summary>
    public string? ReviewerNotes { get; set; }
}

/// <summary>
/// Appeal note/comment
/// </summary>
public class AppealNote
{
    public string NoteId { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public string NoteText { get; set; } = string.Empty;
    public bool IsInternal { get; set; } = true;
}

public enum AppealType
{
    Reconsideration,  // Standard appeal review
    PeerReview,       // Clinical peer-to-peer review
    ExternalReview,   // Independent external review
    Grievance         // Member-initiated grievance
}

public enum AppealLevel
{
    FirstLevel,      // Initial appeal review
    SecondLevel,     // Second-level appeal
    ExternalReview   // State/Federal external review
}

public enum AppealStatus
{
    Draft,           // Being prepared
    Submitted,       // Submitted to payer
    InReview,        // Under review by payer
    PendingInfo,     // Waiting for additional information
    Approved,        // Appeal approved
    Denied,          // Appeal denied
    PartialApproval, // Partially approved
    Withdrawn        // Withdrawn by provider
}

public enum AppealDecisionType
{
    Approved,
    Denied,
    PartialApproval
}

public enum AttachmentStatus
{
    Pending,      // Not yet sent
    Sent,         // 275 transaction sent
    Acknowledged, // Payer acknowledged receipt
    Rejected,     // Payer rejected attachment
    Error         // Transmission error
}

public enum LineOfBusiness
{
    Commercial,
    Medicare,
    Medicaid,
    Marketplace
}

/// <summary>
/// Appeals summary statistics
/// </summary>
public class AppealsSummary
{
    public int TotalAppeals { get; set; }
    public int InReview { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    public int PartialApprovals { get; set; }
    public decimal TotalAppealedAmount { get; set; }
    public decimal TotalApprovedAmount { get; set; }
    public double AverageDecisionTimeDays { get; set; }
    public double ApprovalRate { get; set; }
    public Dictionary<AppealStatus, int> AppealsByStatus { get; set; } = new();
    public Dictionary<AppealLevel, int> AppealsByLevel { get; set; } = new();
}
