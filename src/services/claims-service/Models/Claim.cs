using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// Represents a healthcare claim (837 transaction)
/// Links to Provider, Member, Coverage, and BenefitPlan services for adjudication
/// </summary>
[BsonIgnoreExtraElements]
public class Claim
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique claim identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Claim number (payer-assigned unique identifier)
    /// 837: CLM01
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ClaimNumber { get; set; } = string.Empty;

    /// <summary>
    /// Member ID (the individual receiving services; may differ from subscriber for dependents)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Subscriber ID (the policy holder; same as MemberId for self-coverage)
    /// Required for family accumulator aggregation.
    /// 837: NM109 (2010BA)
    /// </summary>
    [StringLength(50)]
    public string? SubscriberId { get; set; }

    /// <summary>
    /// Benefit plan ID (links to BenefitPlanService for cost-sharing rules)
    /// Required for accumulator grouping by plan year.
    /// </summary>
    [StringLength(50)]
    public string? BenefitPlanId { get; set; }

    /// <summary>
    /// Coverage ID (links to Coverage Service for eligibility)
    /// </summary>
    [StringLength(50)]
    public string? CoverageId { get; set; }

    /// <summary>
    /// Subscriber first name
    /// 837: NM103 (2010BA)
    /// </summary>
    [StringLength(100)]
    public string? SubscriberFirstName { get; set; }

    /// <summary>
    /// Subscriber last name
    /// 837: NM102 (2010BA)
    /// </summary>
    [StringLength(100)]
    public string? SubscriberLastName { get; set; }

    /// <summary>
    /// Patient first name (if different from subscriber)
    /// 837: NM103 (2010CA)
    /// </summary>
    [StringLength(100)]
    public string? PatientFirstName { get; set; }

    /// <summary>
    /// Patient last name (if different from subscriber)
    /// 837: NM102 (2010CA)
    /// </summary>
    [StringLength(100)]
    public string? PatientLastName { get; set; }

    /// <summary>
    /// Patient relationship to subscriber
    /// 837: PAT01 (18=Self, 01=Spouse, 19=Child)
    /// </summary>
    [StringLength(2)]
    public string? PatientRelationship { get; set; }

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Billing provider NPI (rendering provider who performed service)
    /// 837: NM109 (2010AA)
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
    /// Rendering provider NPI (if different from billing)
    /// 837: NM109 (2310B)
    /// </summary>
    [StringLength(10)]
    public string? RenderingProviderNPI { get; set; }

    /// <summary>
    /// Rendering provider name
    /// </summary>
    [StringLength(300)]
    public string? RenderingProviderName { get; set; }

    /// <summary>
    /// Facility NPI (place of service)
    /// 837: NM109 (2310C)
    /// </summary>
    [StringLength(10)]
    public string? FacilityNPI { get; set; }

    /// <summary>
    /// Facility name
    /// </summary>
    [StringLength(300)]
    public string? FacilityName { get; set; }

    /// <summary>
    /// Place of service code
    /// 837: CLM05-1 (11=Office, 21=Inpatient Hospital, 22=Outpatient Hospital, 23=Emergency Room)
    /// </summary>
    [Required]
    [StringLength(2)]
    public string PlaceOfServiceCode { get; set; } = "11";

    /// <summary>
    /// Claim type (Professional, Institutional, Dental)
    /// 837P = Professional, 837I = Institutional, 837D = Dental
    /// </summary>
    [Required]
    public ClaimType ClaimType { get; set; } = ClaimType.Professional;

    /// <summary>
    /// Claim frequency code
    /// 837: CLM05-3 (1=Original, 7=Replacement, 8=Void)
    /// </summary>
    [StringLength(1)]
    public string ClaimFrequencyCode { get; set; } = "1";

    /// <summary>
    /// Total claim charge amount (sum of all service lines)
    /// 837: CLM02
    /// </summary>
    [Required]
    [Range(0, 999999999.99)]
    public decimal TotalChargeAmount { get; set; }

    /// <summary>
    /// Service date (from - start of service period)
    /// 837: DTP*472 (professional) or DTP*434 (institutional)
    /// </summary>
    [Required]
    public DateTime ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date (to - end of service period)
    /// 837: DTP*472 (professional) or DTP*435 (institutional)
    /// </summary>
    [Required]
    public DateTime ServiceDateTo { get; set; }

    /// <summary>
    /// Diagnosis codes (ICD-10)
    /// 837: HI segment (ABK = Principal Diagnosis, ABF = Secondary Diagnosis)
    /// </summary>
    public List<DiagnosisCode> DiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Claim service lines (procedures)
    /// 837: 2400 loop (service line details)
    /// </summary>
    public List<ClaimLine> ClaimLines { get; set; } = new();

    /// <summary>
    /// Claim status
    /// </summary>
    [Required]
    public ClaimStatus Status { get; set; } = ClaimStatus.Submitted;

    /// <summary>
    /// Claim submission date
    /// </summary>
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Claim received date (by payer)
    /// </summary>
    public DateTime? ReceivedDate { get; set; }

    /// <summary>
    /// Adjudication date (when claim was processed)
    /// </summary>
    public DateTime? AdjudicatedDate { get; set; }

    /// <summary>
    /// Paid date (when payment was issued - 835 transaction)
    /// </summary>
    public DateTime? PaidDate { get; set; }

    /// <summary>
    /// Adjudication result (approved, denied, pending)
    /// </summary>
    public AdjudicationResult? AdjudicationResult { get; set; }

    /// <summary>
    /// Structured detail about why this claim is in Pended status. Populated by the
    /// adjudication workflow when a deterministic edit fails (e.g., NCCI/MUE).
    /// Distinct from AdjudicationResult so the deterministic pend reason cannot be
    /// silently overwritten by a downstream consumer.
    /// </summary>
    public PendDetails? PendDetails { get; set; }

    /// <summary>
    /// Advisory recommendation from the AI Claims Examiner service. Always advisory:
    /// the deterministic pipeline remains authoritative, and a human examiner approves,
    /// modifies, or overrides the recommendation via the work queue. Stored separately
    /// from AdjudicationResult to keep the AI/audit boundary explicit.
    /// </summary>
    public AiExamination? AiExamination { get; set; }

    /// <summary>
    /// Prior authorization number (if required)
    /// 837: REF*G1 (2300 loop)
    /// </summary>
    [StringLength(50)]
    public string? PriorAuthorizationNumber { get; set; }

    /// <summary>
    /// Referral number (if applicable)
    /// 837: REF*9F (2300 loop)
    /// </summary>
    [StringLength(50)]
    public string? ReferralNumber { get; set; }

    /// <summary>
    /// Related-causes code (AA=Auto Accident, EM=Employment, OA=Other Accident).
    /// Null when the service is unrelated to any accident/injury liability.
    /// 837: CLM11-1 (2300 loop)
    /// </summary>
    [StringLength(2)]
    public string? RelatedCausesCode { get; set; }

    /// <summary>
    /// Accident date. Set only when <see cref="RelatedCausesCode"/> is set.
    /// 837: DTP*439 (2300 loop)
    /// </summary>
    public DateTime? AccidentDate { get; set; }

    /// <summary>
    /// Claim notes/comments
    /// 837: NTE segment
    /// </summary>
    [StringLength(2000)]
    public string? ClaimNotes { get; set; }

    /// <summary>
    /// EDI 837 transaction control number (for tracking)
    /// </summary>
    [StringLength(50)]
    public string? EDI837ControlNumber { get; set; }

    /// <summary>
    /// EDI 835 remittance control number (for payment tracking)
    /// </summary>
    [StringLength(50)]
    public string? EDI835ControlNumber { get; set; }

    // Version identity (5.1 — Claim Identity & Versioning)
    //
    // A claim is an append-only chain of immutable terminal versions. Each
    // row in the Claims collection is one version; the chain is keyed on
    // (TenantId, ClaimVersionId) — ClaimVersionId is the persistent claim
    // identifier, while Id is the per-version document key. Documents
    // written before these fields existed hydrate to ClaimVersionId=Id,
    // VersionNumber=1, and a VersionState derived from the legacy
    // ClaimStatus (Submitted/Pended → Submitted; Approved → Adjudicated;
    // Paid/PartiallyPaid → Paid; Denied → Denied; Voided → Voided). See
    // docs/architecture/claim-versioning.md.
    //
    // The legacy ClaimStatus enum is preserved as the operational
    // sub-state signal: ClaimStatus.Pended, .Received, .InAdjudication,
    // .Approved, .PartiallyPaid all remain transient pipeline outcomes
    // within their respective ClaimVersionState. This avoids breaking the
    // 22 existing controller endpoints and the accumulator-service Kafka
    // contract while introducing the audit chain semantics.

    /// <summary>
    /// Stable per-chain identifier — same value across every version of a
    /// single claim. Set explicitly by the service layer when a draft is
    /// created. Empty on the wire ⇒ legacy row (predates this feature) and
    /// is hydrated to <c>Id</c> on read.
    /// </summary>
    public string ClaimVersionId { get; set; } = string.Empty;

    /// <summary>
    /// 1-based monotonic sequence within <c>(TenantId, ClaimVersionId)</c>.
    /// Populated by the service when creating new versions; left at the
    /// default for legacy documents so hydration can fix it up on read.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// Lifecycle state of this claim version. Populated by the service when
    /// creating new versions; legacy documents missing this field
    /// deserialize to <see cref="ClaimVersionState.Unknown"/> and are
    /// normalized during hydration based on the legacy <see cref="Status"/>
    /// value.
    /// </summary>
    public ClaimVersionState VersionState { get; set; }

    /// <summary>
    /// <see cref="Id"/> of the version this draft amends, if any. Null for
    /// the genesis version. Populated by the adjustment workflow (5.12).
    /// </summary>
    [StringLength(64)]
    public string? PredecessorVersionId { get; set; }

    /// <summary>
    /// UTC timestamp when this version transitioned out of <c>Draft</c>
    /// (i.e. when <c>Submitted</c> was first reached). Mirrors
    /// <c>BenefitPlan.PublishedAt</c>.
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Actor who published the version (left Draft).</summary>
    [StringLength(200)]
    public string? PublishedBy { get; set; }

    /// <summary>
    /// UTC timestamp when this version was superseded by an adjustment.
    /// Set together with <see cref="SupersededByVersionId"/> when the
    /// version transitions to <see cref="ClaimVersionState.Adjusted"/>.
    /// </summary>
    public DateTime? SupersededAt { get; set; }

    /// <summary>
    /// <see cref="Id"/> of the adjustment version that replaced this one.
    /// Set together with <see cref="SupersededAt"/> when the version
    /// transitions to <see cref="ClaimVersionState.Adjusted"/>.
    /// </summary>
    [StringLength(64)]
    public string? SupersededByVersionId { get; set; }

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

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
/// 837: HI segment
/// </summary>
[BsonIgnoreExtraElements]
public class DiagnosisCode
{
    /// <summary>
    /// ICD-10 diagnosis code (e.g., E11.9 = Type 2 diabetes)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Diagnosis type qualifier
    /// ABK = Principal Diagnosis
    /// ABF = Secondary Diagnosis
    /// </summary>
    [StringLength(3)]
    public string CodeQualifier { get; set; } = "ABK";

    /// <summary>
    /// Pointer number (1-12) for linking to service lines
    /// </summary>
    public int PointerNumber { get; set; }

    /// <summary>
    /// Diagnosis description (for display)
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }
}

/// <summary>
/// Claim service line (procedure)
/// 837: 2400 loop
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimLine
{
    /// <summary>
    /// Line number (sequence within claim)
    /// 837: LX01
    /// </summary>
    [Required]
    public int LineNumber { get; set; }

    /// <summary>
    /// Procedure code (CPT/HCPCS)
    /// 837: SV101-2 (professional) or SV201-2 (institutional)
    /// </summary>
    [Required]
    [StringLength(10)]
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Procedure description (for display)
    /// </summary>
    [StringLength(500)]
    public string? ProcedureDescription { get; set; }

    /// <summary>
    /// Procedure modifiers (up to 4)
    /// 837: SV101-3, SV101-4, SV101-5, SV101-6
    /// </summary>
    public List<string> Modifiers { get; set; } = new();

    /// <summary>
    /// Diagnosis code pointers (links to diagnosis codes)
    /// 837: SV107
    /// </summary>
    public List<int> DiagnosisPointers { get; set; } = new();

    /// <summary>
    /// Units of service (quantity)
    /// 837: SV104 (professional) or SV205 (institutional)
    /// </summary>
    [Required]
    [Range(0, 9999)]
    public decimal Units { get; set; } = 1;

    /// <summary>
    /// Charge amount per unit
    /// 837: SV102 (professional) or SV202 (institutional)
    /// </summary>
    [Required]
    [Range(0, 999999.99)]
    public decimal ChargeAmount { get; set; }

    /// <summary>
    /// Service date (from)
    /// 837: DTP*472
    /// </summary>
    [Required]
    public DateTime ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date (to)
    /// 837: DTP*472
    /// </summary>
    [Required]
    public DateTime ServiceDateTo { get; set; }

    /// <summary>
    /// Place of service code (can override claim-level)
    /// </summary>
    [StringLength(2)]
    public string? PlaceOfServiceCode { get; set; }

    /// <summary>
    /// Revenue code (for institutional claims)
    /// 837I: SV201
    /// </summary>
    [StringLength(4)]
    public string? RevenueCode { get; set; }

    /// <summary>
    /// MPIP rate multiplier applied to this line's allowed amount during adjudication.
    /// 1.063 if FL SMMC 3.0 enhanced rate applies, null if MPIP was not evaluated.
    /// </summary>
    public decimal? MpipMultiplierApplied { get; set; }

    /// <summary>
    /// Adjudication result for this line
    /// </summary>
    public LineAdjudicationResult? AdjudicationResult { get; set; }
}

/// <summary>
/// Adjudication result (claim-level)
/// Populated by claims adjudication workflow and 835 remittance
/// </summary>
[BsonIgnoreExtraElements]
public class AdjudicationResult
{
    /// <summary>
    /// Network tier used to adjudicate this claim.
    /// Determines which accumulator bucket (InNetwork / OutOfNetwork / OutOfArea) is updated.
    /// Populated by the calculate-cost-sharing workflow step.
    /// </summary>
    [StringLength(20)]
    public string? NetworkTier { get; set; }

    /// <summary>
    /// Allowed amount (what payer will pay based on contracted rates)
    /// 835: CLP04
    /// </summary>
    public decimal AllowedAmount { get; set; }

    /// <summary>
    /// Deductible amount (member responsibility - deductible not met)
    /// 835: CAS segment (PR-1)
    /// </summary>
    public decimal DeductibleAmount { get; set; }

    /// <summary>
    /// Coinsurance amount (member responsibility - % after deductible)
    /// 835: CAS segment (PR-2)
    /// </summary>
    public decimal CoinsuranceAmount { get; set; }

    /// <summary>
    /// Copay amount (member responsibility - fixed amount)
    /// 835: CAS segment (PR-3)
    /// </summary>
    public decimal CopayAmount { get; set; }

    /// <summary>
    /// Total patient responsibility (deductible + coinsurance + copay)
    /// 835: CLP05
    /// </summary>
    public decimal PatientResponsibility { get; set; }

    /// <summary>
    /// Payer payment amount (what payer will pay provider)
    /// 835: CLP04 - patient responsibility
    /// </summary>
    public decimal PayerPayment { get; set; }

    /// <summary>
    /// Denial reason code (if denied)
    /// 835: CAS02 (CO = Contractual, PR = Patient Responsibility, PI = Payer Initiated)
    /// </summary>
    [StringLength(10)]
    public string? DenialReasonCode { get; set; }

    /// <summary>
    /// Denial reason description
    /// </summary>
    [StringLength(500)]
    public string? DenialReason { get; set; }

    /// <summary>
    /// Claim adjustment reason codes (CARC)
    /// 835: CAS segment
    /// </summary>
    public List<ClaimAdjustmentReason> AdjustmentReasons { get; set; } = new();

    /// <summary>
    /// Remark codes (additional info)
    /// 835: LQ segment
    /// </summary>
    public List<string> RemarkCodes { get; set; } = new();

    /// <summary>
    /// Check/EFT number (for payment tracking)
    /// 835: TRN02
    /// </summary>
    [StringLength(50)]
    public string? CheckNumber { get; set; }

    /// <summary>
    /// Payment date
    /// 835: DTM*405
    /// </summary>
    public DateTime? PaymentDate { get; set; }
}

/// <summary>
/// Line-level adjudication result
/// 835: 2110 loop (service payment information)
/// </summary>
public class LineAdjudicationResult
{
    /// <summary>
    /// Allowed amount for this line
    /// 835: SVC03
    /// </summary>
    public decimal AllowedAmount { get; set; }

    /// <summary>
    /// Paid amount for this line
    /// 835: SVC03 - adjustments
    /// </summary>
    public decimal PaidAmount { get; set; }

    /// <summary>
    /// Patient responsibility for this line
    /// </summary>
    public decimal PatientResponsibility { get; set; }

    /// <summary>
    /// Adjustment reasons for this line
    /// 835: CAS segment (line-level)
    /// </summary>
    public List<ClaimAdjustmentReason> AdjustmentReasons { get; set; } = new();
}

/// <summary>
/// Claim adjustment reason code
/// 835: CAS segment
/// </summary>
public class ClaimAdjustmentReason
{
    /// <summary>
    /// Group code
    /// CO = Contractual Obligation
    /// PR = Patient Responsibility
    /// PI = Payer Initiated Reduction
    /// OA = Other Adjustments
    /// </summary>
    [Required]
    [StringLength(2)]
    public string GroupCode { get; set; } = string.Empty;

    /// <summary>
    /// Reason code (CARC - Claim Adjustment Reason Code)
    /// Examples: 1=Deductible, 2=Coinsurance, 3=Copay, 45=Late filing
    /// </summary>
    [Required]
    [StringLength(10)]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Adjustment amount
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Reason description
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }
}

/// <summary>
/// Claim type (837 transaction set identifier)
/// </summary>
public enum ClaimType
{
    /// <summary>
    /// 837P - Professional (physician, clinic)
    /// </summary>
    Professional = 1,

    /// <summary>
    /// 837I - Institutional (hospital, facility)
    /// </summary>
    Institutional = 2,

    /// <summary>
    /// 837D - Dental
    /// </summary>
    Dental = 3
}

/// <summary>
/// Claim status (lifecycle)
/// Updated by 277 claim status transactions
/// </summary>
public enum ClaimStatus
{
    /// <summary>
    /// Initial submission (837 sent)
    /// </summary>
    Submitted = 1,

    /// <summary>
    /// Received by payer (277 acknowledgment)
    /// </summary>
    Received = 2,

    /// <summary>
    /// In adjudication (being processed)
    /// </summary>
    InAdjudication = 3,

    /// <summary>
    /// Pended (waiting for additional info)
    /// 277 status code 16
    /// </summary>
    Pended = 4,

    /// <summary>
    /// Approved (adjudication complete, payment authorized)
    /// </summary>
    Approved = 5,

    /// <summary>
    /// Denied (adjudication complete, no payment)
    /// 277 status code 4
    /// </summary>
    Denied = 6,

    /// <summary>
    /// Paid (835 remittance processed)
    /// 277 status code 2
    /// </summary>
    Paid = 7,

    /// <summary>
    /// Voided (reversed/cancelled)
    /// </summary>
    Voided = 8,

    /// <summary>
    /// Partially paid (some lines approved, some denied)
    /// </summary>
    PartiallyPaid = 9
}

/// <summary>
/// Line of Business enum (matches other services)
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

/// <summary>
/// Why a claim was placed in Pended status. Written by the adjudication workflow
/// at the moment of the pend; never mutated by downstream consumers (the AI examiner
/// service writes its output to AiExamination, not here).
/// </summary>
[BsonIgnoreExtraElements]
public class PendDetails
{
    /// <summary>
    /// Short pend reason code consumed by the work queue categorizer.
    /// Recognized values: NCCI, MUE, AUTH, NOAUTH, OON, NOCONTRACT, COB, MEDREVIEW, CLINICAL, RETROELIG, SUBRO, SPENDDOWN.
    /// </summary>
    [Required]
    [StringLength(20)]
    public string PendCode { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the pend reason.
    /// </summary>
    [StringLength(500)]
    public string? PendReason { get; set; }

    /// <summary>
    /// UTC timestamp when the claim was pended.
    /// </summary>
    public DateTime PendedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// NCCI/MUE edit failures that caused the pend. Empty for non-edit pends.
    /// </summary>
    public List<NcciEditFailureSnapshot> EditFailures { get; set; } = new();
}

/// <summary>
/// Claim-service-local snapshot of an NCCI engine edit failure. Mirrors
/// CloudHealthOffice.NcciEngine.Models.NcciEditFailure but lives here so the
/// claims-service does not take a hard reference on the engine assembly.
/// </summary>
[BsonIgnoreExtraElements]
public class NcciEditFailureSnapshot
{
    /// <summary>NCCI_PAIR or MUE.</summary>
    [StringLength(20)]
    public string EditType { get; set; } = string.Empty;

    /// <summary>NE001 (NCCI bundling) or NE002 (MUE).</summary>
    [StringLength(10)]
    public string RuleId { get; set; } = string.Empty;

    /// <summary>Human-readable description of the failure.</summary>
    [StringLength(1000)]
    public string? Message { get; set; }

    /// <summary>Column 1 procedure code (NCCI pair edits only).</summary>
    [StringLength(10)]
    public string? Column1Code { get; set; }

    /// <summary>Column 2 procedure code (NCCI pair edits only).</summary>
    [StringLength(10)]
    public string? Column2Code { get; set; }

    /// <summary>Claim line numbers affected by the edit.</summary>
    public List<int> AffectedLineNumbers { get; set; } = new();

    /// <summary>
    /// True if a -59/X{EPSU} modifier was already present at submission. The AI examiner
    /// is only invoked for edits where this is the legal override path; see
    /// IsModifierAddressable() for the v1 selection rule.
    /// </summary>
    public bool ModifierOverridePresent { get; set; }

    /// <summary>For MUE failures: units billed.</summary>
    public decimal? UnitsBilled { get; set; }

    /// <summary>For MUE failures: MUE max units limit.</summary>
    public int? MueMaxUnits { get; set; }

    /// <summary>Suggested CARC for the EOB/835.</summary>
    [StringLength(10)]
    public string? SuggestedCarc { get; set; }

    /// <summary>Suggested RARC remark code.</summary>
    [StringLength(10)]
    public string? SuggestedRarc { get; set; }

    /// <summary>
    /// True when the edit type is one a modifier could legally override.
    /// v1 of the AI examiner only acts on NCCI pair edits with ModifierIndicator = 1,
    /// which the engine surfaces as RuleId NE001 with ModifierOverridePresent reflecting
    /// what the submitter sent. The examiner reviews whether a -59/X{EPSU} should have
    /// been billed; MUE/unit-limit edits are out of scope for v1.
    /// </summary>
    public bool IsModifierAddressable() =>
        string.Equals(EditType, "NcciPair", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RuleId, "NE001", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Advisory recommendation produced by the AI Claims Examiner service for a pended claim.
/// Always advisory — the deterministic pipeline remains authoritative and a human
/// examiner must accept, modify, or override before any payment-impacting action.
/// </summary>
[BsonIgnoreExtraElements]
public class AiExamination
{
    /// <summary>
    /// Recommended disposition: Approve, Deny, RequestInfo, EscalateToHuman.
    /// EscalateToHuman is the safe default when the model declines to commit.
    /// </summary>
    [Required]
    [StringLength(20)]
    public string RecommendedDisposition { get; set; } = "EscalateToHuman";

    /// <summary>
    /// Model self-reported confidence in the recommendation, 0.0–1.0.
    /// Used by the work queue to band claims for relaxed-threshold experiments
    /// once override-rate data is available.
    /// </summary>
    [Range(0, 1)]
    public double ConfidenceScore { get; set; }

    /// <summary>
    /// Plain-English rationale for the disposition. Shown to the examiner alongside
    /// the claim. Capped at 4000 chars; the model is prompted to be concise.
    /// </summary>
    [StringLength(4000)]
    public string? Rationale { get; set; }

    /// <summary>
    /// Citations to the policy/rule the model relied on (e.g., "NCCI Manual Ch.1 §F.3",
    /// "CMS NCCI 2025Q1 column1=27447 column2=27486 modifier_indicator=1").
    /// Empty when no citation could be produced — that itself is a signal.
    /// </summary>
    public List<string> PolicyCitations { get; set; } = new();

    /// <summary>
    /// Anthropic model ID used to produce this recommendation (e.g., "claude-opus-4-6").
    /// Pinned per call so we can correlate quality with model version.
    /// </summary>
    [StringLength(100)]
    public string? ModelId { get; set; }

    /// <summary>
    /// Internal prompt template version (e.g., "ncci-pend-v1"). Lets us A/B prompt
    /// revisions without losing the ability to attribute outcomes to a specific prompt.
    /// </summary>
    [StringLength(50)]
    public string? PromptVersion { get; set; }

    /// <summary>
    /// UTC timestamp when the recommendation was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when a human examiner acts on the claim. Null while the claim sits in the
    /// queue. Values: Accepted, Modified, Overridden. This is the feedback signal the
    /// 90-day override-rate analysis depends on; do not skip writing it on examiner action.
    /// </summary>
    [StringLength(20)]
    public string? ExaminerAgreement { get; set; }

    /// <summary>
    /// UTC timestamp when ExaminerAgreement was set.
    /// </summary>
    public DateTime? ExaminerActedAt { get; set; }

    /// <summary>
    /// Examiner who acted on the claim (set with ExaminerAgreement).
    /// </summary>
    [StringLength(200)]
    public string? ExaminerUserId { get; set; }
}
