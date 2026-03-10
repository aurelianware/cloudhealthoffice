namespace CloudHealthOffice.EncounterEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// ENUMS
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// X12 claim type code — determines whether 837P (professional) or
/// 837I (institutional) transaction format is generated.
/// </summary>
public enum ClaimFormType
{
    /// <summary>837P — CMS-1500 equivalent (professional services).</summary>
    Professional,

    /// <summary>837I — UB-04 equivalent (institutional / hospital).</summary>
    Institutional
}

/// <summary>
/// Lifecycle state of an encounter submission.
/// </summary>
public enum EncounterStatus
{
    /// <summary>Encounter built from adjudication data; not yet batched.</summary>
    Pending,

    /// <summary>Included in a batch and transmitted to the regulatory receiver.</summary>
    Submitted,

    /// <summary>Receiver acknowledged and accepted (999 TA).</summary>
    Accepted,

    /// <summary>Receiver rejected the encounter (999 TR or 824 application rejection).</summary>
    Rejected,

    /// <summary>Corrected encounter submitted to replace a previously accepted one.</summary>
    Corrected,

    /// <summary>Encounter voided / reversed (CLM05-3 = 8).</summary>
    Voided
}

/// <summary>
/// Type code for CLM05-3 — claim frequency code.
/// Regulatory receivers use this to route original vs. correction vs. void.
/// </summary>
public enum ClaimFrequencyCode
{
    /// <summary>Original — first submission for this date-of-service/provider.</summary>
    Original = 1,

    /// <summary>Corrected — replaces a previously accepted encounter.</summary>
    Corrected = 7,

    /// <summary>Void / Reversal — cancels a previously accepted encounter.</summary>
    Void = 8
}

// ═══════════════════════════════════════════════════════════════════
// ENCOUNTER INPUT — built from the adjudication result
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// All information needed to produce an X12 837 encounter transaction.
/// The encounter-service populates this from the adjudicated claim, member,
/// and provider records immediately after adjudication completes.
/// </summary>
public record EncounterInput
{
    // ── Identifiers ──────────────────────────────────────────────────

    /// <summary>Internal claim identifier (source system claim number).</summary>
    public string ClaimId { get; init; } = default!;

    /// <summary>Tenant / plan identifier.</summary>
    public string TenantId { get; init; } = default!;

    /// <summary>
    /// For corrected or voided encounters: the original encounter control number
    /// to reference in the REF*F8 segment.
    /// </summary>
    public string? OriginalEncounterControlNumber { get; init; }

    /// <summary>Whether this is an original, corrected, or void submission.</summary>
    public ClaimFrequencyCode FrequencyCode { get; init; } = ClaimFrequencyCode.Original;

    // ── Claim meta ────────────────────────────────────────────────────

    public ClaimFormType FormType { get; init; } = ClaimFormType.Professional;
    public DateOnly ServiceDate { get; init; }
    public DateOnly? AdmitDate { get; init; }
    public DateOnly? DischargeDate { get; init; }
    public string? DrgCode { get; init; }
    public string PlaceOfService { get; init; } = default!;
    public bool IsEmergency { get; init; }

    // ── Member / subscriber ───────────────────────────────────────────

    public string MemberId { get; init; } = default!;
    public string SubscriberId { get; init; } = default!;
    public string MemberFirstName { get; init; } = default!;
    public string MemberLastName { get; init; } = default!;
    public DateOnly MemberDateOfBirth { get; init; }
    public string MemberGender { get; init; } = "U"; // X12: M/F/U

    // ── Provider ──────────────────────────────────────────────────────

    public string BillingNpi { get; init; } = default!;
    public string BillingProviderName { get; init; } = default!;
    public string BillingTaxId { get; init; } = default!;
    public string? RenderingNpi { get; init; }
    public string? RenderingProviderLastName { get; init; }
    public string? RenderingProviderFirstName { get; init; }

    // ── Plan / submitter ──────────────────────────────────────────────

    /// <summary>The managed care plan's NPI / submitter ID (ISA06).</summary>
    public string PlanSubmitterId { get; init; } = default!;

    /// <summary>The regulatory receiver ID (CMS, state Medicaid — ISA08).</summary>
    public string ReceiverSubmitterId { get; init; } = default!;

    /// <summary>Plan name for NM1*PR loop.</summary>
    public string PlanName { get; init; } = default!;

    /// <summary>Plan payer ID (GS02/GS03).</summary>
    public string PlanPayerId { get; init; } = default!;

    // ── Diagnosis codes ───────────────────────────────────────────────

    /// <summary>ICD-10-CM diagnosis codes in order (first = principal/primary).</summary>
    public List<string> DiagnosisCodes { get; init; } = [];

    // ── Adjudication results ──────────────────────────────────────────

    /// <summary>Per-line adjudication results — source for SV1/SV2, AMT, QTY segments.</summary>
    public List<EncounterLineInput> Lines { get; init; } = [];

    // ── COB context (populated when member has dual coverage) ─────────

    /// <summary>When non-null, OI and MOA/MIA segments are emitted for the other payer.</summary>
    public EncounterCobContext? Cob { get; init; }
}

/// <summary>
/// Adjudication result for one claim line, as needed by the encounter transformer.
/// </summary>
public record EncounterLineInput
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public string? CodeType { get; init; } = "HC"; // HC=CPT/HCPCS, AD=ADA, NU=NDC
    public List<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal PlanPaidAmount { get; init; }
    public decimal MemberResponsibility { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public decimal Units { get; init; } = 1;
    public List<string> DiagnosisPointers { get; init; } = ["1"]; // points to DiagnosisCodes index
    public bool IsDenied { get; init; }
    public string? DenialReasonCode { get; init; }

    // COB amounts (populated when secondary claim)
    public decimal PrimaryPayerPayment { get; init; }
    public decimal CobReduction { get; init; }
}

/// <summary>
/// COB information to embed in encounter OI/MOA segments.
/// </summary>
public record EncounterCobContext
{
    /// <summary>Other payer's name (NM1*TT).</summary>
    public string OtherPayerName { get; init; } = default!;

    /// <summary>Other payer's ID (NM109).</summary>
    public string OtherPayerId { get; init; } = default!;

    /// <summary>Total amount the other (primary) payer paid across all lines.</summary>
    public decimal OtherPayerPaidAmount { get; init; }

    /// <summary>Payer responsibility sequence: P=Primary, S=Secondary, T=Tertiary.</summary>
    public string PayerResponsibilityCode { get; init; } = "P";
}

// ═══════════════════════════════════════════════════════════════════
// ENCOUNTER OUTPUT — the produced EDI and metadata
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Single encounter record — one CLM transaction in the 837.
/// </summary>
public record EncounterRecord
{
    /// <summary>The encounter control number — used as BHT03 and CLM's REF*D9.</summary>
    public string EncounterControlNumber { get; init; } = default!;

    public string ClaimId { get; init; } = default!;
    public string TenantId { get; init; } = default!;
    public EncounterStatus Status { get; init; } = EncounterStatus.Pending;
    public ClaimFormType FormType { get; init; }
    public DateOnly ServiceDate { get; init; }

    /// <summary>Raw X12 837 transaction set (ST through SE) for this encounter.</summary>
    public string RawX12 { get; init; } = default!;

    /// <summary>Total billed amount (CLM02).</summary>
    public decimal TotalBilled { get; init; }

    /// <summary>Total plan paid amount (for MOA/AMT reporting).</summary>
    public decimal TotalPlanPaid { get; init; }

    /// <summary>Total member responsibility.</summary>
    public decimal TotalMemberResponsibility { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; init; }
}

/// <summary>
/// A batch of encounter transactions — one ISA/GS envelope.
/// </summary>
public record EncounterBatch
{
    public string BatchId { get; init; } = default!;
    public string TenantId { get; init; } = default!;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int TransactionCount { get; init; }

    /// <summary>Raw X12 file — ISA through IEA, containing all ST/SE transactions.</summary>
    public string RawX12 { get; init; } = default!;

    /// <summary>Encounter control numbers included in this batch.</summary>
    public List<string> EncounterControlNumbers { get; init; } = [];
}
