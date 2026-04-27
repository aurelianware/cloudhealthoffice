using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AuthorizationService.Models;

/// <summary>
/// Represents a prior authorization request (278 transaction)
/// Required before submitting claims for certain procedures/services
/// </summary>
public class Authorization
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique authorization identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Authorization number (payer-assigned unique identifier)
    /// 278: REF*BB (2000E)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string AuthorizationNumber { get; set; } = string.Empty;

    /// <summary>
    /// Member ID (subscriber ID)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Coverage ID (links to Coverage Service for eligibility)
    /// </summary>
    [StringLength(50)]
    public string? CoverageId { get; set; }

    /// <summary>
    /// Patient first name
    /// 278: NM103 (2010C)
    /// </summary>
    [Required]
    [StringLength(100)]
    public string PatientFirstName { get; set; } = string.Empty;

    /// <summary>
    /// Patient last name
    /// 278: NM102 (2010C)
    /// </summary>
    [Required]
    [StringLength(100)]
    public string PatientLastName { get; set; } = string.Empty;

    /// <summary>
    /// Patient date of birth
    /// 278: DMG02 (2010C)
    /// </summary>
    [Required]
    public DateTime PatientDateOfBirth { get; set; }

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Requesting provider NPI (who is asking for authorization)
    /// 278: NM109 (2010A)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string RequestingProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Requesting provider name
    /// </summary>
    [StringLength(300)]
    public string? RequestingProviderName { get; set; }

    /// <summary>
    /// Servicing provider NPI (who will perform the service - may differ from requesting)
    /// 278: NM109 (2010B)
    /// </summary>
    [StringLength(10)]
    public string? ServicingProviderNPI { get; set; }

    /// <summary>
    /// Servicing provider name
    /// </summary>
    [StringLength(300)]
    public string? ServicingProviderName { get; set; }

    /// <summary>
    /// Facility NPI (where service will be performed)
    /// 278: NM109 (2010E)
    /// </summary>
    [StringLength(10)]
    public string? FacilityNPI { get; set; }

    /// <summary>
    /// Facility name
    /// </summary>
    [StringLength(300)]
    public string? FacilityName { get; set; }

    /// <summary>
    /// Authorization type
    /// </summary>
    [Required]
    public AuthorizationType AuthorizationType { get; set; }

    /// <summary>
    /// Certification type
    /// 278: UM02 (2000E)
    /// I = Initial, R = Renewal, S = Recertification
    /// </summary>
    [StringLength(1)]
    public string CertificationType { get; set; } = "I";

    /// <summary>
    /// Service type code
    /// 278: UM03 (2000E)
    /// 1 = Medical Care, 2 = Surgical, 3 = Consultation, 33 = Chiropractic, 35 = Dental, 47 = Diagnostic Medical, 48 = Chronic Renal Disease, 76 = Dialysis, 86 = Emergency Services, 88 = Pharmacy, 98 = Urgent Care
    /// </summary>
    [Required]
    [StringLength(2)]
    public string ServiceTypeCode { get; set; } = string.Empty;

    /// <summary>
    /// Level of service
    /// 278: UM04 (2000E)
    /// U = Urgent, E = Elective
    /// </summary>
    [StringLength(1)]
    public string? LevelOfService { get; set; }

    /// <summary>
    /// Requested service date (from)
    /// 278: DTP*291 (2000E)
    /// </summary>
    [Required]
    public DateTime RequestedServiceDateFrom { get; set; }

    /// <summary>
    /// Requested service date (to)
    /// 278: DTP*291 (2000E)
    /// </summary>
    public DateTime? RequestedServiceDateTo { get; set; }

    /// <summary>
    /// Diagnosis codes (ICD-10) - reason for authorization
    /// 278: HI segment (2000E)
    /// </summary>
    public List<DiagnosisCode> DiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Requested services/procedures
    /// 278: 2000F loop (service review)
    /// </summary>
    public List<RequestedService> RequestedServices { get; set; } = new();

    /// <summary>
    /// Clinical information/attachments
    /// 278: PWK segment (2000E)
    /// </summary>
    public List<ClinicalAttachment> ClinicalAttachments { get; set; } = new();

    /// <summary>
    /// Authorization status
    /// </summary>
    [Required]
    public AuthorizationStatus Status { get; set; } = AuthorizationStatus.Submitted;

    /// <summary>
    /// Review decision
    /// 278 Response: UM06 (2000E)
    /// A1 = Certified, A2 = Modified, A3 = Denied, A4 = Pended
    /// </summary>
    [StringLength(2)]
    public string? ReviewDecision { get; set; }

    /// <summary>
    /// Approved units (if approved/modified)
    /// </summary>
    public decimal? ApprovedUnits { get; set; }

    /// <summary>
    /// Approved service date (from)
    /// </summary>
    public DateTime? ApprovedServiceDateFrom { get; set; }

    /// <summary>
    /// Approved service date (to)
    /// </summary>
    public DateTime? ApprovedServiceDateTo { get; set; }

    /// <summary>
    /// Denial reason code
    /// 278 Response: HCR segment
    /// </summary>
    [StringLength(10)]
    public string? DenialReasonCode { get; set; }

    /// <summary>
    /// Denial reason description
    /// </summary>
    [StringLength(500)]
    public string? DenialReason { get; set; }

    /// <summary>
    /// Pend reason (if pending additional information)
    /// </summary>
    [StringLength(500)]
    public string? PendReason { get; set; }

    /// <summary>
    /// Follow-up action description (if pended)
    /// 278 Response: MSG segment
    /// </summary>
    [StringLength(1000)]
    public string? FollowUpAction { get; set; }

    /// <summary>
    /// RFAI (Request for Additional Information) reference number
    /// Generated when authorization is pended (A4 status)
    /// Links to 275 attachment submissions
    /// 277 Response: TRN02
    /// </summary>
    [StringLength(50)]
    public string? RFAIReference { get; set; }

    /// <summary>
    /// Whether RFAI has been issued for this authorization
    /// </summary>
    public bool RFAIIssued { get; set; } = false;

    /// <summary>
    /// Date RFAI was issued (277 sent to provider)
    /// </summary>
    public DateTime? RFAIIssuedDate { get; set; }

    /// <summary>
    /// Date attachments were received in response to RFAI (275 received)
    /// </summary>
    public DateTime? RFAIResponseDate { get; set; }

    /// <summary>
    /// Reviewer name (medical reviewer who made decision)
    /// </summary>
    [StringLength(200)]
    public string? ReviewerName { get; set; }

    /// <summary>
    /// Reviewer contact phone
    /// 278 Response: PER segment
    /// </summary>
    [StringLength(20)]
    public string? ReviewerPhone { get; set; }

    /// <summary>
    /// Request submission date
    /// </summary>
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Review completion date
    /// </summary>
    public DateTime? ReviewedDate { get; set; }

    /// <summary>
    /// Authorization expiration date
    /// 278 Response: DTP*292 (2000E)
    /// </summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// Related claim number (if claim submitted using this auth)
    /// </summary>
    [StringLength(50)]
    public string? RelatedClaimNumber { get; set; }

    /// <summary>
    /// Notes/comments
    /// </summary>
    [StringLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// EDI 278 request control number
    /// </summary>
    [StringLength(50)]
    public string? EDI278RequestControlNumber { get; set; }

    /// <summary>
    /// EDI 278 response control number
    /// </summary>
    [StringLength(50)]
    public string? EDI278ResponseControlNumber { get; set; }

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when SLA clock restarted after RFAI docs received.
    /// Used for turnaround calculation: decision time measured from
    /// max(SubmittedDate, SlaResumedAt) when RFAI was issued.
    /// Null if no RFAI was issued for this authorization.
    /// </summary>
    public DateTime? SlaResumedAt { get; set; }

    /// <summary>
    /// Current SLA escalation level, set by the deadline watchdog.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SlaEscalationLevel SlaEscalation { get; set; } = SlaEscalationLevel.None;

    /// <summary>
    /// Timestamp of last SLA escalation event.
    /// </summary>
    public DateTime? SlaEscalatedAt { get; set; }

    /// <summary>
    /// Audit: Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// Diagnosis code (ICD-10)
/// 278: HI segment
/// </summary>
public class DiagnosisCode
{
    /// <summary>
    /// ICD-10 diagnosis code
    /// </summary>
    [Required]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Diagnosis type qualifier
    /// BK = Principal Diagnosis
    /// BF = Secondary Diagnosis
    /// </summary>
    [StringLength(2)]
    public string CodeQualifier { get; set; } = "BK";

    /// <summary>
    /// Diagnosis description
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }
}

/// <summary>
/// Requested service/procedure
/// 278: 2000F loop
/// </summary>
public class RequestedService
{
    /// <summary>
    /// Procedure code (CPT/HCPCS)
    /// 278: SV101 (2000F)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Procedure description
    /// </summary>
    [StringLength(500)]
    public string? ProcedureDescription { get; set; }

    /// <summary>
    /// Procedure modifiers
    /// </summary>
    public List<string> Modifiers { get; set; } = new();

    /// <summary>
    /// Requested units/quantity
    /// 278: SV104 (2000F)
    /// </summary>
    [Required]
    public decimal RequestedUnits { get; set; } = 1;

    /// <summary>
    /// Unit type (visits, days, sessions, etc.)
    /// 278: SV105 (2000F)
    /// </summary>
    [StringLength(10)]
    public string? UnitType { get; set; }

    /// <summary>
    /// Place of service code
    /// 278: SV109 (2000F)
    /// </summary>
    [StringLength(2)]
    public string? PlaceOfServiceCode { get; set; }

    /// <summary>
    /// Revenue code (for facility services)
    /// </summary>
    [StringLength(4)]
    public string? RevenueCode { get; set; }

    /// <summary>
    /// Approved units (if modified from requested)
    /// </summary>
    public decimal? ApprovedUnits { get; set; }

    /// <summary>
    /// Service-level status (approved, denied, modified)
    /// </summary>
    [StringLength(2)]
    public string? ServiceStatus { get; set; }

    /// <summary>
    /// Service-level denial reason
    /// </summary>
    [StringLength(500)]
    public string? DenialReason { get; set; }
}

/// <summary>
/// Clinical attachment/documentation
/// 278: PWK segment
/// </summary>
public class ClinicalAttachment
{
    /// <summary>
    /// Attachment type
    /// 278: PWK01
    /// 03 = Report Justifying Treatment, 04 = Drugs Administered, 77 = Support Data for Claim
    /// </summary>
    [Required]
    [StringLength(2)]
    public string AttachmentType { get; set; } = string.Empty;

    /// <summary>
    /// Transmission code
    /// 278: PWK02
    /// EL = Electronic, EM = Email, FX = Fax
    /// </summary>
    [StringLength(2)]
    public string TransmissionCode { get; set; } = "EL";

    /// <summary>
    /// Description
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// File URL (if stored in blob storage)
    /// </summary>
    [StringLength(500)]
    public string? FileUrl { get; set; }

    /// <summary>
    /// File name
    /// </summary>
    [StringLength(200)]
    public string? FileName { get; set; }

    /// <summary>
    /// File size (bytes)
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Upload date
    /// </summary>
    public DateTime? UploadedDate { get; set; }
}

/// <summary>
/// Authorization type
/// </summary>
public enum AuthorizationType
{
    /// <summary>
    /// Not specified / default (used for uninitialized or legacy records)
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Pre-authorization (before service)
    /// </summary>
    PreAuthorization = 1,

    /// <summary>
    /// Concurrent review (during service)
    /// </summary>
    ConcurrentReview = 2,

    /// <summary>
    /// Retrospective review (after service)
    /// </summary>
    RetrospectiveReview = 3,

    /// <summary>
    /// Referral authorization
    /// </summary>
    Referral = 4
}

/// <summary>
/// Authorization status
/// </summary>
public enum AuthorizationStatus
{
    /// <summary>
    /// Submitted to payer (278 request sent)
    /// </summary>
    Submitted = 1,

    /// <summary>
    /// Under medical review
    /// </summary>
    InReview = 2,

    /// <summary>
    /// Pended (waiting for additional information)
    /// 278 Response: A4
    /// </summary>
    Pended = 3,

    /// <summary>
    /// Approved/Certified (all requested services approved)
    /// 278 Response: A1
    /// </summary>
    Approved = 4,

    /// <summary>
    /// Modified (some services approved, some denied/reduced)
    /// 278 Response: A2
    /// </summary>
    Modified = 5,

    /// <summary>
    /// Denied (all requested services denied)
    /// 278 Response: A3
    /// </summary>
    Denied = 6,

    /// <summary>
    /// Expired (authorization period ended)
    /// </summary>
    Expired = 7,

    /// <summary>
    /// Cancelled (withdrawn by provider/member)
    /// </summary>
    Cancelled = 8
}

/// <summary>
/// SLA escalation level, set by the deadline watchdog.
/// </summary>
public enum SlaEscalationLevel
{
    None,
    Warning,
    Critical,
    Breach
}

/// <summary>
/// Line of Business enum (matches other services)
/// </summary>
public enum LineOfBusiness
{
    Unknown = 0,
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}
