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
    /// Pay-to address: 837 Loop 2010AB (NM1*87 N3/N4). In 5010 this loop is
    /// an address only (NM103 onward are not used): the billing provider is
    /// the pay-to provider, and this is the address to direct payment to when
    /// it differs from the billing provider's. Null when the 837 had none.
    /// 835: payee N3/N4 (1000B).
    /// </summary>
    public ClaimAddress? PayToAddress { get; set; }

    /// <summary>
    /// Pay-to plan: 837 Loop 2010AC (NM1*PE), sent on a subrogation demand
    /// (BHT06 = 31), the only case it is used (SNIP L4-2010AC-BHT06). The plan, not the billing
    /// provider, is the entity to be paid. Null when the 837 had none.
    /// </summary>
    public ClaimPayToPlan? PayToPlan { get; set; }

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
    /// Institutional (837I / UB-04) header detail: facility type, admission
    /// and discharge, statement period, CL1 codes, DRG, ICD-10-PCS
    /// procedures and occurrence/span/value/condition codes. Null for
    /// professional and dental claims and for institutional documents
    /// written before these fields existed.
    /// </summary>
    public InstitutionalClaimDetails? Institutional { get; set; }

    /// <summary>
    /// 837 2000B SBR01: this plan's payer responsibility sequence for the
    /// claim (P primary, S secondary, T tertiary, A–H payers four to eleven,
    /// U unknown). Null when the claim did not come from an 837 carrying it.
    /// </summary>
    [StringLength(1)]
    public string? PayerResponsibilityCode { get; set; }

    /// <summary>
    /// 837 loops 2320 / 2330B / 2430: the other payers on the claim and what
    /// each one that already adjudicated paid and adjusted, at claim and line
    /// level. Feeds the benefit engine's coordination-of-benefits input.
    /// </summary>
    public List<ClaimOtherPayer> OtherPayers { get; set; } = new();

    /// <summary>
    /// 837 2430 loops whose SVD01 matches none of the claim's other payers
    /// (2330B NM109, REF*2U, REF*FY) while there are several: they cannot be
    /// placed in the payer order, so the COB stage pends the claim instead of
    /// undercounting what the earlier payers paid.
    /// </summary>
    public List<ClaimOtherPayerLine> UnmatchedOtherPayerLines { get; set; } = new();

    /// <summary>
    /// Three-character type of bill (UB-04 FL4 without the leading zero):
    /// the two-digit facility type code (837I CLM05-1) followed by the
    /// claim frequency code (CLM05-3, <see cref="ClaimFrequencyCode"/>).
    /// Derived, never stored, so it cannot drift from the frequency code
    /// when an adjustment re-versions the claim. Null when the facility
    /// type is unknown (all non-institutional claims).
    /// </summary>
    [BsonIgnore]
    public string? TypeOfBill =>
        InstitutionalClaimDetails.ComposeTypeOfBill(Institutional?.FacilityTypeCode, ClaimFrequencyCode);

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
    /// Audit trail of examiner resolutions of this claim's pends (PR #1278
    /// round 3): who approved or denied it, why, the payer order they
    /// confirmed, and exactly which pends their decision overrode. Persisted
    /// on the claim and carried on the ClaimVersionResolved event.
    /// </summary>
    public List<ExaminerResolutionRecord> ExaminerResolutions { get; set; } = new();

    /// <summary>
    /// A first approval waiting for a second, different approver (sequence
    /// 1 over a prior payer that paid). Null when none is waiting.
    /// </summary>
    public PendingExaminerApproval? PendingExaminerApproval { get; set; }

    /// <summary>
    /// Held while an examiner resolution re-adjudicates the claim, so two
    /// concurrent approvals cannot both re-run and publish (taken with a
    /// conditional write on Pended + no live lock).
    /// </summary>
    public ExaminerResolutionLock? ResolutionLock { get; set; }

    /// <summary>
    /// Accumulator outbox: the engine commit this claim's final adjudication
    /// owes (deductible / OOP / visit counts), written in the same conditional
    /// write that finalizes it (the pipeline's Approved status patch, or the
    /// examiner approval's lock-fenced replace) and cleared once the engine
    /// store has it (<c>AccumulatorOutboxDispatcher</c>). A crash between the
    /// finalizing write and the commit, or benefit-plan-service being down,
    /// leaves it here to be driven again — idempotently, by commit id.
    /// </summary>
    public AccumulatorOutboxItem? PendingAccumulatorCommit { get; set; }

    /// <summary>
    /// Accumulator outbox: the terminal reversal (fence) an examiner's denial
    /// owes, written with the denial's lock-fenced final write and driven the
    /// same way.
    /// </summary>
    public AccumulatorOutboxItem? PendingAccumulatorReversal { get; set; }

    /// <summary>
    /// Set when the engine store clamped this claim's commit at a limit (a
    /// concurrent claim for the member or family took the room its pricing
    /// saw): the member's priced cost share was not changed — an examiner
    /// reviews it (work queue <c>accumulator-adjustments</c>).
    /// </summary>
    public AccumulatorClampReview? AccumulatorClampReview { get; set; }

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

    /// <summary>
    /// Inbound payer 835 remittance id applied by <c>IRemittancePoster</c>.
    /// Distinct from CHO-as-payer outbound <see cref="EDI835ControlNumber"/>.
    /// </summary>
    [StringLength(100)]
    public string? InboundRemittanceId { get; set; }

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

    /// <summary>
    /// Present-on-admission indicator (Y, N, U, W, or 1 for exempt).
    /// Institutional only.
    /// 837I: HI0x-9
    /// </summary>
    [StringLength(1)]
    public string? PresentOnAdmission { get; set; }
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
    /// Line-item charge amount: the TOTAL billed for this line across all
    /// <see cref="Units"/> (NOT a per-unit price). Σ ChargeAmount over all
    /// lines must equal the claim's <see cref="Claim.TotalChargeAmount"/>
    /// (CLM02 balancing, scrub rule AL002) and flows unchanged to the
    /// 835 SVC02.
    /// 837: SV102 (professional) or SV203 (institutional)
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
    /// National Drug Code (11-digit, 5-4-2) for a drug billed on this line.
    /// 837P/837I: LIN03 (2410, LIN02 = N4)
    /// </summary>
    [StringLength(11)]
    public string? NationalDrugCode { get; set; }

    /// <summary>
    /// Quantity of the drug identified by <see cref="NationalDrugCode"/>.
    /// 837P/837I: CTP04 (2410)
    /// </summary>
    public decimal? DrugQuantity { get; set; }

    /// <summary>
    /// Unit of measure for <see cref="DrugQuantity"/> (F2, GR, ME, ML, UN).
    /// 837P/837I: CTP05-1 (2410)
    /// </summary>
    [StringLength(2)]
    public string? DrugUnitOfMeasure { get; set; }

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

/// <summary>A postal address carried on a claim (837 N3/N4).</summary>
[BsonIgnoreExtraElements]
public class ClaimAddress
{
    /// <summary>N301.</summary>
    [StringLength(55)]
    public string Line1 { get; set; } = string.Empty;

    /// <summary>N302.</summary>
    [StringLength(55)]
    public string? Line2 { get; set; }

    /// <summary>N401.</summary>
    [StringLength(30)]
    public string City { get; set; } = string.Empty;

    /// <summary>N402.</summary>
    [StringLength(2)]
    public string? State { get; set; }

    /// <summary>N403.</summary>
    [StringLength(15)]
    public string? PostalCode { get; set; }

    /// <summary>N404; null for a US address.</summary>
    [StringLength(3)]
    public string? CountryCode { get; set; }
}

/// <summary>
/// 837 Loop 2010AC pay-to plan. Its tax id is the TIN of the entity to be
/// paid for the subrogation (X12 RFI 1107).
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimPayToPlan
{
    /// <summary>NM103 pay-to plan organizational name.</summary>
    [StringLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>NM108: PI (payor identification) or XV (CMS plan id).</summary>
    [StringLength(2)]
    public string? IdentifierQualifier { get; set; }

    /// <summary>NM109 pay-to plan primary identifier.</summary>
    [StringLength(80)]
    public string? Identifier { get; set; }

    /// <summary>REF*EI REF02 pay-to plan tax identification number.</summary>
    [StringLength(50)]
    public string? TaxId { get; set; }

    /// <summary>N3/N4 pay-to plan address.</summary>
    public ClaimAddress? Address { get; set; }
}

/// <summary>
/// Institutional (837I / UB-04) claim header detail. Additive: a document
/// without it deserializes to <c>null</c>.
///
/// <para>
/// The DRG is the one <i>billed</i> on the claim (HI*DR). CHO does not run
/// an MS-DRG grouper; a DRG-priced claim without a billed DRG finds no DRG
/// rate and pends for pricing rather than being grouped.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class InstitutionalClaimDetails
{
    /// <summary>
    /// Facility type code — the first two digits of the type of bill
    /// (e.g. 11 = hospital inpatient, 13 = hospital outpatient).
    /// 837I: CLM05-1 (CLM05-2 = A)
    /// </summary>
    [StringLength(2)]
    public string? FacilityTypeCode { get; set; }

    /// <summary>
    /// Inpatient admission (or start of care) date.
    /// 837I: DTP*435 (DT or D8)
    /// </summary>
    public DateTime? AdmissionDate { get; set; }

    /// <summary>
    /// Admission hour (HHMM) when the admission date was sent in DT format.
    /// 837I: DTP*435 (DT, positions 9-12)
    /// </summary>
    [StringLength(4)]
    public string? AdmissionHour { get; set; }

    /// <summary>
    /// Discharge date. 005010X223 carries no discharge-date segment — for a
    /// discharged inpatient the discharge date is
    /// <see cref="StatementToDate"/>. Populated only when a DTP*096 arrives
    /// with a D8/DT date instead of the spec's TM hour (an internal
    /// encounter-generator shape).
    /// </summary>
    public DateTime? DischargeDate { get; set; }

    /// <summary>
    /// Discharge hour (HHMM).
    /// 837I: DTP*096 (TM)
    /// </summary>
    [StringLength(4)]
    public string? DischargeHour { get; set; }

    /// <summary>
    /// Statement covers period — from.
    /// 837I: DTP*434 (RD8)
    /// </summary>
    public DateTime? StatementFromDate { get; set; }

    /// <summary>
    /// Statement covers period — through.
    /// 837I: DTP*434 (RD8)
    /// </summary>
    public DateTime? StatementToDate { get; set; }

    /// <summary>
    /// Priority (type) of admission or visit (1=Emergency, 2=Urgent, 3=Elective...).
    /// 837I: CL101
    /// </summary>
    [StringLength(1)]
    public string? AdmissionTypeCode { get; set; }

    /// <summary>
    /// Point of origin (source) for admission or visit.
    /// 837I: CL102
    /// </summary>
    [StringLength(1)]
    public string? AdmissionSourceCode { get; set; }

    /// <summary>
    /// Patient (discharge) status (01=Home, 03=SNF, 20=Expired, 30=Still a patient...).
    /// 837I: CL103
    /// </summary>
    [StringLength(2)]
    public string? PatientStatusCode { get; set; }

    /// <summary>
    /// Diagnosis related group billed by the facility (e.g. MS-DRG 470).
    /// 837I: HI*DR
    /// </summary>
    [StringLength(4)]
    public string? DrgCode { get; set; }

    /// <summary>
    /// Principal ICD-10-PCS procedure.
    /// 837I: HI*BBR
    /// </summary>
    public InstitutionalProcedureCode? PrincipalProcedure { get; set; }

    /// <summary>
    /// Other ICD-10-PCS procedures.
    /// 837I: HI*BBQ
    /// </summary>
    public List<InstitutionalProcedureCode> OtherProcedures { get; set; } = new();

    /// <summary>837I: HI*BH (occurrence code + date).</summary>
    public List<OccurrenceCode> OccurrenceCodes { get; set; } = new();

    /// <summary>837I: HI*BI (occurrence span code + period).</summary>
    public List<OccurrenceSpanCode> OccurrenceSpanCodes { get; set; } = new();

    /// <summary>837I: HI*BE (value code + amount).</summary>
    public List<ValueCode> ValueCodes { get; set; } = new();

    /// <summary>837I: HI*BG (condition codes).</summary>
    public List<string> ConditionCodes { get; set; } = new();

    /// <summary>
    /// Builds the three-character type of bill from the facility type code
    /// and the claim frequency code. Null unless both parts are present and
    /// well-formed (two-character facility type, one-character frequency).
    /// </summary>
    public static string? ComposeTypeOfBill(string? facilityTypeCode, string? claimFrequencyCode) =>
        facilityTypeCode is { Length: 2 } && claimFrequencyCode is { Length: 1 }
            ? facilityTypeCode + claimFrequencyCode
            : null;

    /// <summary>
    /// Days billed on this claim for per-diem pricing: discharge (or
    /// statement-through) date minus admission date. When the statement
    /// period starts after the admission (an interim or continuing bill),
    /// counting starts at the statement-from date so each bill covers only
    /// its own days; with no admission date, the statement covers period is
    /// used. The discharge day is not counted (midnight census), except that
    /// a patient still in house (status 30, interim bill) is counted through
    /// the statement-through date. A same-day stay counts as one day. Null
    /// when the dates needed are missing or inverted.
    /// </summary>
    public int? CalculateLengthOfStay()
    {
        var through = DischargeDate ?? StatementToDate;
        var from = AdmissionDate is { } admitted && StatementFromDate is { } statementFrom
            ? (statementFrom.Date > admitted.Date ? statementFrom : admitted)
            : AdmissionDate ?? StatementFromDate;
        if (from is null || through is null || through.Value.Date < from.Value.Date)
        {
            return null;
        }

        var days = (through.Value.Date - from.Value.Date).Days;
        if (PatientStatusCode == "30")
        {
            days += 1;
        }
        return Math.Max(days, 1);
    }
}

/// <summary>ICD-10-PCS procedure on an institutional claim (837I HI*BBR / HI*BBQ).</summary>
[BsonIgnoreExtraElements]
public class InstitutionalProcedureCode
{
    /// <summary>ICD-10-PCS code (e.g. 0SR9019).</summary>
    [StringLength(7)]
    public string Code { get; set; } = string.Empty;

    /// <summary>HI qualifier: BBR (principal) or BBQ (other).</summary>
    [StringLength(3)]
    public string CodeQualifier { get; set; } = "BBR";

    /// <summary>Procedure date (HI0x-4, D8).</summary>
    public DateTime? Date { get; set; }
}

/// <summary>UB-04 occurrence code (837I HI*BH).</summary>
[BsonIgnoreExtraElements]
public class OccurrenceCode
{
    [StringLength(2)]
    public string Code { get; set; } = string.Empty;

    public DateTime? Date { get; set; }
}

/// <summary>UB-04 occurrence span code (837I HI*BI).</summary>
[BsonIgnoreExtraElements]
public class OccurrenceSpanCode
{
    [StringLength(2)]
    public string Code { get; set; } = string.Empty;

    public DateTime? FromDate { get; set; }

    public DateTime? ToDate { get; set; }
}

/// <summary>UB-04 value code (837I HI*BE).</summary>
[BsonIgnoreExtraElements]
public class ValueCode
{
    [StringLength(2)]
    public string Code { get; set; } = string.Empty;

    public decimal? Amount { get; set; }
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
    /// Portion of <see cref="PatientResponsibility"/> that counts toward the
    /// out-of-pocket maximum, as computed by the benefit engine. Lower than
    /// patient responsibility when the benefit excludes cost share from the
    /// OOP max (<c>OopApplies = false</c>). Null on claims adjudicated before
    /// the field existed or by a path that doesn't compute it; readers fall
    /// back to <see cref="PatientResponsibility"/>, the prior behavior.
    /// </summary>
    public decimal? OopAppliedAmount { get; set; }

    /// <summary>
    /// Deductible the benefit engine credited to the deductible accumulators.
    /// Differs from the PR-1 deductible only when this plan paid secondary or
    /// later under NAIC full deductible credit (the plan credits the deductible
    /// it would have applied with no other coverage). Null on claims adjudicated
    /// before the field existed; readers fall back to the PR-1 deductible.
    /// </summary>
    public decimal? DeductibleCreditedAmount { get; set; }

    /// <summary>
    /// The payer sequence this plan adjudicated the claim in when COB was
    /// applied (2 secondary, 3 tertiary, 4–11); null when it paid as the first
    /// payer. The 835 CLP02 (processed as primary / secondary / tertiary)
    /// follows this — what was actually applied — not the 837 SBR01 alone.
    /// </summary>
    public int? CobPayerSequence { get; set; }

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
    /// Portion of <see cref="PatientResponsibility"/> that counts toward the
    /// out-of-pocket maximum, as computed by the benefit engine. Lower than
    /// patient responsibility when the benefit excludes cost share from the
    /// OOP max (<c>OopApplies = false</c>). Null on claims adjudicated before
    /// the field existed or by a path that doesn't compute it; readers fall
    /// back to <see cref="PatientResponsibility"/>, the prior behavior.
    /// </summary>
    public decimal? OopAppliedAmount { get; set; }

    /// <summary>
    /// Deductible the benefit engine credited to the deductible accumulators.
    /// Differs from the PR-1 deductible only when this plan paid secondary or
    /// later under NAIC full deductible credit (the plan credits the deductible
    /// it would have applied with no other coverage). Null on claims adjudicated
    /// before the field existed; readers fall back to the PR-1 deductible.
    /// </summary>
    public decimal? DeductibleCreditedAmount { get; set; }

    /// <summary>
    /// Adjustment reasons for this line
    /// 835: CAS segment (line-level)
    /// </summary>
    public List<ClaimAdjustmentReason> AdjustmentReasons { get; set; } = new();
}

/// <summary>
/// Another payer on the claim (837 loop 2320 other subscriber information,
/// 2330B other payer name, 2430 line adjudication information).
/// </summary>
public class ClaimOtherPayer
{
    /// <summary>2320 SBR01 — this payer's responsibility sequence (P/S/T/A–H/U).</summary>
    [StringLength(1)]
    public string PayerResponsibilityCode { get; set; } = string.Empty;

    /// <summary>2330B NM103.</summary>
    [StringLength(60)]
    public string? PayerName { get; set; }

    /// <summary>2330B NM109 (matched by 2430 SVD01).</summary>
    [StringLength(80)]
    public string? PayerId { get; set; }

    /// <summary>2330B REF*2U / REF*FY — other identifiers 2430 SVD01 may name this payer by.</summary>
    public List<string> AdditionalPayerIds { get; set; } = new();

    /// <summary>True when <paramref name="id"/> (a 2430 SVD01) names this payer.</summary>
    public bool IsIdentifiedBy(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && ((PayerId is not null && string.Equals(PayerId.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase))
            || AdditionalPayerIds.Any(a => string.Equals(a?.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>2320 AMT*D — what the payer paid on the claim. Null when not reported.</summary>
    public decimal? PaidAmount { get; set; }

    /// <summary>2320 CAS — the payer's claim-level adjustments.</summary>
    public List<ClaimAdjustmentReason> ClaimAdjustments { get; set; } = new();

    /// <summary>2430 — the payer's line-level adjudication.</summary>
    public List<ClaimOtherPayerLine> LineAdjudications { get; set; } = new();
}

/// <summary>One 837 loop 2430: another payer's adjudication of a claim line.</summary>
public class ClaimOtherPayerLine
{
    /// <summary>The claim line (LX) the 2430 loop belongs to.</summary>
    public int LineNumber { get; set; }

    /// <summary>SVD01 — the identifier the 2430 names its payer by.</summary>
    [StringLength(80)]
    public string? PayerId { get; set; }

    /// <summary>SVD02 — what the payer paid for the line.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>2430 CAS — the payer's adjustments to the line.</summary>
    public List<ClaimAdjustmentReason> Adjustments { get; set; } = new();
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
    /// Remittance Advice Remark Code (RARC) qualifying this adjustment, when
    /// the adjudicator supplied one. 835: LQ (line) / MOA/MIA (claim).
    /// </summary>
    [StringLength(10)]
    public string? RemarkCode { get; set; }

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
    /// Recognized values: NCCI, MUE, AUTH, NOAUTH, OON, NOCONTRACT, COB, MEDREVIEW, CLINICAL, RETROELIG, SUBRO, SPENDDOWN, PRICING, DUPLICATE.
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

    /// <summary>
    /// Duplicate-claim findings that caused (or contributed to) the pend.
    /// Kept apart from the NCCI-specific <see cref="EditFailures"/> so
    /// duplicates are reported as duplicates downstream (transparency,
    /// FHIR EOB) and survive a later stage replacing the pend reason.
    /// Empty when no duplicate was found.
    /// </summary>
    public List<DuplicateFindingSnapshot> DuplicateFindings { get; set; } = new();

    /// <summary>
    /// Further pend reasons ("{code}: {reason}") a later stage found while
    /// the claim was already pended for <see cref="PendCode"/> — e.g. NCCI
    /// edit failures on a claim pended for COB. <see cref="PendCode"/> (which
    /// routes the work queue) stays the first reason; every reason is shown
    /// to the examiner.
    /// </summary>
    public List<string> AdditionalPendReasons { get; set; } = new();

    /// <summary>
    /// Identifies exactly this set of pends: a hash of every "{code}: {reason}"
    /// (the routing pend and every additional one) and of the findings behind
    /// them (NCCI/MUE edit failures, duplicate matches), each sorted ordinally
    /// so the order a stage happened to store them in does not matter. The
    /// examiner's client sends back the fingerprint of the pends they viewed;
    /// an approval whose fingerprint no longer matches the stored pends (a
    /// re-adjudication changed them) is refused with 409 (PR #1278 round-3
    /// verification, M4). Derived; not stored in Mongo.
    /// <para>
    /// It does not include <see cref="PendedAt"/> (follow-up 5): every re-run
    /// sets a new PendedAt, so a failed (transient) approval that re-pended
    /// the claim with the same pends invalidated the examiner's fingerprint
    /// and any waiting first approval. Dropping it does not reopen the
    /// "pends changed between page load and click" hole: a re-adjudication
    /// that changes anything the examiner was shown — a pend, a reason, a
    /// finding — changes the hash; one that produces exactly the same pends
    /// and findings leaves the examiner's review exactly as accurate, and the
    /// approval re-run overrides only the pends matching what was reviewed.
    /// </para>
    /// </summary>
    [BsonIgnore]
    [JsonPropertyName("fingerprint")]
    public string Fingerprint => ComputeFingerprint(this);

    /// <summary>See <see cref="Fingerprint"/>; empty for no pend.</summary>
    public static string ComputeFingerprint(PendDetails? pend)
    {
        if (pend is null) return string.Empty;
        var reasons = new List<string> { $"{pend.PendCode}: {pend.PendReason}" };
        reasons.AddRange(pend.AdditionalPendReasons ?? []);
        reasons.Sort(StringComparer.Ordinal);

        var findings = (pend.EditFailures ?? [])
            .Select(f => string.Join('|', "EDIT", f.EditType, f.RuleId, f.Column1Code, f.Column2Code,
                string.Join(',', (f.AffectedLineNumbers ?? []).Order()),
                f.UnitsBilled?.ToString(System.Globalization.CultureInfo.InvariantCulture), f.MueMaxUnits))
            .Concat((pend.DuplicateFindings ?? [])
                .Select(d => string.Join('|', "DUP", d.DuplicateType, d.RuleId, d.LineNumber,
                    d.MatchedClaimId, d.MatchedLineNumber)))
            .ToList();
        findings.Sort(StringComparer.Ordinal);

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', reasons) + "\n\u001e\n" + string.Join('\n', findings)));
        return $"v2-{Convert.ToHexString(hash, 0, 12).ToLowerInvariant()}";
    }
}

/// <summary>One examiner resolution of a pended claim (see <see cref="Claim.ExaminerResolutions"/>).</summary>
[BsonIgnoreExtraElements]
public class ExaminerResolutionRecord
{
    /// <summary>Approved | Denied.</summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>Every approver, in order (two for a second-approver sign-off).</summary>
    public List<string> ApproverIds { get; set; } = new();

    /// <summary>The last approver's reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Each approver's reason, aligned with <see cref="ApproverIds"/>.</summary>
    public List<string?> ApproverReasons { get; set; } = new();

    /// <summary>The fingerprint of the pends the approver(s) reviewed (<see cref="PendDetails.Fingerprint"/>).</summary>
    public string? PendFingerprint { get; set; }

    /// <summary>The payer order the examiner confirmed for a COB pend.</summary>
    public int? PayerSequence { get; set; }

    /// <summary>The persisted pends the examiner reviewed ("{code}: {reason}").</summary>
    public List<string> ReviewedPends { get; set; } = new();

    /// <summary>The pends the approval re-run overrode ("{stage}: {code}: {reason}").</summary>
    public List<string> OverriddenPends { get; set; } = new();

    /// <summary>When the (first) approver acted.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>When the resolution completed.</summary>
    public DateTime ResolvedAt { get; set; }
}

/// <summary>A first approval waiting for a second approver (see <see cref="Claim.PendingExaminerApproval"/>).</summary>
[BsonIgnoreExtraElements]
public class PendingExaminerApproval
{
    public string RequestedBy { get; set; } = string.Empty;
    public int? PayerSequence { get; set; }
    public string? Reason { get; set; }
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// The pends the first approver reviewed (<see cref="PendDetails.Fingerprint"/>).
    /// A second approval counts only while the claim's pends still match.
    /// </summary>
    public string? PendFingerprint { get; set; }

    /// <summary>After this the first approval no longer counts (configurable TTL, default 72 h).</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>One accumulator outbox entry (see <see cref="Claim.PendingAccumulatorCommit"/>).</summary>
[BsonIgnoreExtraElements]
public class AccumulatorOutboxItem
{
    /// <summary>The commit id (commit), or a reversal id (reversal): the conditional-clear key.</summary>
    public string Id { get; set; } = string.Empty;

    public CloudHealthOffice.BenefitEngine.Models.AccumulatorCommit? Commit { get; set; }

    public AccumulatorReversalOrder? Reversal { get; set; }

    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }

    /// <summary><see cref="CreatedAt"/> / <see cref="NextAttemptAt"/> as Unix milliseconds: what the queries compare (numbers sort the same on every store).</summary>
    public long CreatedAtMs { get; set; }
    public long DueAtMs { get; set; }

    /// <summary>The last failure's exception type and status (no claim data).</summary>
    public string? LastError { get; set; }

    public static AccumulatorOutboxItem ForCommit(CloudHealthOffice.BenefitEngine.Models.AccumulatorCommit commit, DateTime now) => new()
    {
        Id = commit.CommitId,
        Commit = commit,
        CreatedAt = now,
        NextAttemptAt = now,
        CreatedAtMs = ToMs(now),
        DueAtMs = ToMs(now),
    };

    public static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>The terminal reversal of <paramref name="claim"/>; null when its plan id is not a GUID (nothing to reverse).</summary>
    public static AccumulatorOutboxItem? ForReversal(Claim claim, DateTime now) =>
        Guid.TryParse(claim.BenefitPlanId, out _)
            ? new AccumulatorOutboxItem
            {
                Id = $"reversal-{Guid.NewGuid():N}",
                Reversal = new AccumulatorReversalOrder
                {
                    ClaimId = claim.Id,
                    MemberId = claim.MemberId,
                    SubscriberId = string.IsNullOrWhiteSpace(claim.SubscriberId) ? claim.MemberId : claim.SubscriberId!,
                    BenefitPlanId = claim.BenefitPlanId!,
                    ServiceDate = claim.ServiceDateFrom,
                },
                CreatedAt = now,
                NextAttemptAt = now,
                CreatedAtMs = ToMs(now),
                DueAtMs = ToMs(now),
            }
            : null;
}

/// <summary>What a terminal accumulator reversal needs (see <see cref="Claim.PendingAccumulatorReversal"/>).</summary>
[BsonIgnoreExtraElements]
public class AccumulatorReversalOrder
{
    public string ClaimId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string SubscriberId { get; set; } = string.Empty;
    public string BenefitPlanId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
}

/// <summary>See <see cref="Claim.AccumulatorClampReview"/>.</summary>
[BsonIgnoreExtraElements]
public class AccumulatorClampReview
{
    public string CommitId { get; set; } = string.Empty;
    public DateTime RaisedAt { get; set; }
    public List<CloudHealthOffice.BenefitEngine.Models.AccumulatorClamp> Clamps { get; set; } = new();
    public bool Resolved { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Which outbox slot of a claim (<see cref="Claim.PendingAccumulatorCommit"/> or <see cref="Claim.PendingAccumulatorReversal"/>).</summary>
public enum AccumulatorOutboxKind
{
    Commit,
    Reversal,
}

/// <summary>See <see cref="Claim.ResolutionLock"/>.</summary>
[BsonIgnoreExtraElements]
public class ExaminerResolutionLock
{
    public string Token { get; set; } = string.Empty;
    public string? LockedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// One service line flagged by the duplicate-claim stage, with the prior
/// claim line it matched.
/// </summary>
[BsonIgnoreExtraElements]
public class DuplicateFindingSnapshot
{
    /// <summary>"Exact" or "Suspect".</summary>
    [StringLength(20)]
    public string DuplicateType { get; set; } = string.Empty;

    /// <summary>DUP001 (exact) or DUP002 (suspect).</summary>
    [StringLength(10)]
    public string RuleId { get; set; } = string.Empty;

    /// <summary>Human-readable description of the match.</summary>
    [StringLength(1000)]
    public string? Message { get; set; }

    /// <summary>Line number on this claim that was flagged.</summary>
    public int LineNumber { get; set; }

    /// <summary>Per-version id of the prior claim the line matched.</summary>
    [StringLength(64)]
    public string? MatchedClaimId { get; set; }

    /// <summary>Claim number of the prior claim the line matched.</summary>
    [StringLength(50)]
    public string? MatchedClaimNumber { get; set; }

    /// <summary>Line number on the prior claim that was matched.</summary>
    public int? MatchedLineNumber { get; set; }

    /// <summary>Suggested CARC for the EOB/835 (18 — exact duplicate claim/service).</summary>
    [StringLength(10)]
    public string? SuggestedCarc { get; set; }
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
