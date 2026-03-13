using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EncounterService.Models;

/// <summary>
/// Represents a healthcare encounter submission record.
/// Tracks the lifecycle of an encounter from initial submission through
/// batch dispatch, payer acknowledgment, and any correction/resubmission cycles.
///
/// An encounter wraps one or more claim-level records (sourced from Claims Service)
/// and tracks the EDI 837 submission envelope sent to a trading partner / payer.
/// </summary>
public class Encounter
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique encounter identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Encounter control number — payer-assigned or system-generated tracking ID.
    /// Maps to the 837 ISA13 / GS06 control numbers for the submitted batch.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string EncounterControlNumber { get; set; } = string.Empty;

    /// <summary>
    /// Source claim ID (from Claims Service) that this encounter wraps.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Source claim number (human-readable).
    /// </summary>
    [StringLength(50)]
    public string? ClaimNumber { get; set; }

    /// <summary>
    /// Member ID (the individual who received services).
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Subscriber ID (policy holder).
    /// </summary>
    [StringLength(50)]
    public string? SubscriberId { get; set; }

    /// <summary>
    /// Subscriber first name (837: NM103 2010BA)
    /// </summary>
    [StringLength(100)]
    public string? SubscriberFirstName { get; set; }

    /// <summary>
    /// Subscriber last name (837: NM102 2010BA)
    /// </summary>
    [StringLength(100)]
    public string? SubscriberLastName { get; set; }

    /// <summary>
    /// Patient first name (if different from subscriber; 837: NM103 2010CA)
    /// </summary>
    [StringLength(100)]
    public string? PatientFirstName { get; set; }

    /// <summary>
    /// Patient last name (if different from subscriber; 837: NM102 2010CA)
    /// </summary>
    [StringLength(100)]
    public string? PatientLastName { get; set; }

    /// <summary>
    /// Billing provider NPI (837: NM109 2010AA)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string BillingProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Billing provider name
    /// </summary>
    [StringLength(300)]
    public string? BillingProviderName { get; set; }

    /// <summary>
    /// Rendering provider NPI (if different from billing; 837: NM109 2310B)
    /// </summary>
    [StringLength(10)]
    public string? RenderingProviderNPI { get; set; }

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Encounter type (Professional 837P, Institutional 837I, Dental 837D)
    /// </summary>
    [Required]
    public EncounterType EncounterType { get; set; } = EncounterType.Professional;

    /// <summary>
    /// Submission type — Original, Correction (void/replace), or Resubmission
    /// </summary>
    [Required]
    public SubmissionType SubmissionType { get; set; } = SubmissionType.Original;

    /// <summary>
    /// Claim frequency code.
    /// 1 = Original, 7 = Replacement, 8 = Void (837 CLM05-3)
    /// </summary>
    [StringLength(1)]
    public string ClaimFrequencyCode { get; set; } = "1";

    /// <summary>
    /// Original encounter ID that this record corrects/replaces (for corrections).
    /// </summary>
    [StringLength(50)]
    public string? OriginalEncounterId { get; set; }

    /// <summary>
    /// Original encounter control number (for correction reference).
    /// Populated when SubmissionType is Correction or Resubmission.
    /// </summary>
    [StringLength(50)]
    public string? OriginalEncounterControlNumber { get; set; }

    /// <summary>
    /// Payer ID / Trading partner that receives the 837.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PayerId { get; set; } = string.Empty;

    /// <summary>
    /// Payer name
    /// </summary>
    [StringLength(300)]
    public string? PayerName { get; set; }

    /// <summary>
    /// Place of service code (837: CLM05-1)
    /// </summary>
    [StringLength(2)]
    public string PlaceOfServiceCode { get; set; } = "11";

    /// <summary>
    /// Total charge amount (sum of all service lines; 837: CLM02)
    /// </summary>
    [Required]
    [Range(0, 999999999.99)]
    public decimal TotalChargeAmount { get; set; }

    /// <summary>
    /// Service date from (837: DTP*472 or DTP*434)
    /// </summary>
    [Required]
    public DateTime ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date to (837: DTP*472 or DTP*435)
    /// </summary>
    [Required]
    public DateTime ServiceDateTo { get; set; }

    /// <summary>
    /// Diagnosis codes (ICD-10; 837: HI segment)
    /// </summary>
    public List<EncounterDiagnosis> DiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Service lines (procedures; 837: 2400 loop)
    /// </summary>
    public List<EncounterServiceLine> ServiceLines { get; set; } = new();

    /// <summary>
    /// Current lifecycle status of this encounter submission.
    /// </summary>
    [Required]
    public EncounterStatus Status { get; set; } = EncounterStatus.Pending;

    /// <summary>
    /// Batch ID this encounter was dispatched in (null if not yet batched).
    /// </summary>
    [StringLength(50)]
    public string? BatchId { get; set; }

    /// <summary>
    /// Date the encounter was created in the system.
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date the encounter was submitted to the payer.
    /// </summary>
    public DateTime? SubmittedDate { get; set; }

    /// <summary>
    /// Date the payer acknowledged receipt (999/277CA).
    /// </summary>
    public DateTime? AcknowledgedDate { get; set; }

    /// <summary>
    /// Date the encounter was accepted by the payer.
    /// </summary>
    public DateTime? AcceptedDate { get; set; }

    /// <summary>
    /// Date the encounter was rejected by the payer.
    /// </summary>
    public DateTime? RejectedDate { get; set; }

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// EDI 837 interchange control number (ISA13).
    /// </summary>
    [StringLength(50)]
    public string? Edi837InterchangeControlNumber { get; set; }

    /// <summary>
    /// EDI 837 group control number (GS06).
    /// </summary>
    [StringLength(50)]
    public string? Edi837GroupControlNumber { get; set; }

    /// <summary>
    /// EDI 837 transaction set control number (ST02).
    /// </summary>
    [StringLength(50)]
    public string? Edi837TransactionControlNumber { get; set; }

    /// <summary>
    /// 999 Functional Acknowledgment status (A=Accepted, R=Rejected, E=Accepted with Errors)
    /// </summary>
    [StringLength(5)]
    public string? Edi999Status { get; set; }

    /// <summary>
    /// Payer acknowledgment / rejection reasons.
    /// </summary>
    public List<EncounterRejectionReason> RejectionReasons { get; set; } = new();

    /// <summary>
    /// Notes / comments about this encounter submission.
    /// </summary>
    [StringLength(2000)]
    public string? Notes { get; set; }

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
/// Diagnosis code attached to an encounter (ICD-10).
/// </summary>
public class EncounterDiagnosis
{
    [Required]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    [StringLength(3)]
    public string CodeQualifier { get; set; } = "ABK";

    public int PointerNumber { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }
}

/// <summary>
/// Service line (procedure) within an encounter (837: 2400 loop).
/// </summary>
public class EncounterServiceLine
{
    [Required]
    public int LineNumber { get; set; }

    [Required]
    [StringLength(10)]
    public string ProcedureCode { get; set; } = string.Empty;

    [StringLength(500)]
    public string? ProcedureDescription { get; set; }

    public List<string> Modifiers { get; set; } = new();

    public List<int> DiagnosisPointers { get; set; } = new();

    [Required]
    [Range(0, 9999)]
    public decimal Units { get; set; } = 1;

    [Required]
    [Range(0, 999999.99)]
    public decimal ChargeAmount { get; set; }

    [Required]
    public DateTime ServiceDateFrom { get; set; }

    [Required]
    public DateTime ServiceDateTo { get; set; }

    [StringLength(2)]
    public string? PlaceOfServiceCode { get; set; }

    [StringLength(4)]
    public string? RevenueCode { get; set; }
}

/// <summary>
/// Rejection / error reason from payer acknowledgment (999 or 277CA).
/// </summary>
public class EncounterRejectionReason
{
    [Required]
    [StringLength(10)]
    public string ReasonCode { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(50)]
    public string? LoopId { get; set; }

    [StringLength(10)]
    public string? SegmentId { get; set; }

    public int? ElementPosition { get; set; }
}

/// <summary>
/// Encounter type (maps to 837 transaction set variant).
/// </summary>
public enum EncounterType
{
    Professional = 1,   // 837P
    Institutional = 2,  // 837I
    Dental = 3          // 837D
}

/// <summary>
/// Encounter submission lifecycle status.
/// </summary>
public enum EncounterStatus
{
    /// <summary>Created but not yet queued for submission.</summary>
    Pending = 1,

    /// <summary>Queued in a batch awaiting dispatch.</summary>
    Queued = 2,

    /// <summary>Batch dispatched — 837 sent to trading partner.</summary>
    Submitted = 3,

    /// <summary>999 Functional Acknowledgment received — accepted.</summary>
    Acknowledged = 4,

    /// <summary>Payer accepted the encounter (277CA accepted).</summary>
    Accepted = 5,

    /// <summary>Payer rejected the encounter (277CA or 999 rejection).</summary>
    Rejected = 6,

    /// <summary>Correction submitted (void + replace).</summary>
    CorrectionSubmitted = 7,

    /// <summary>Voided / reversed by submitter.</summary>
    Voided = 8
}

/// <summary>
/// Submission type for the encounter.
/// </summary>
public enum SubmissionType
{
    Original = 1,
    Correction = 2,
    Resubmission = 3,
    Void = 4
}

/// <summary>
/// Line of Business enum (consistent with Claims Service).
/// </summary>
public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}
