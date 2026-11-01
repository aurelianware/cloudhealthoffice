using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Licensed;

// ═══════════════════════════════════════════════════════════════════
// LICENSED GROUPER SEAM (APR-DRG, EAPG)
//
// State Medicaid programs price hospital claims with groupers CHO cannot
// ship: 3M/Solventum APR-DRG for inpatient stays (Texas, California and
// most APR-DRG states) and EAPG for outpatient hospital visits (Texas).
// Both are licensed per plan. These types are the contract a plan's own
// licensed grouper answers through; CHO never reimplements the logic.
//
// What a grouper returns is a classification, never a price. The weight
// and base rate come from the state's published tables, which the pricing
// methods load separately, so a plan can swap grouper vendors without
// touching a rate.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// The outcome of asking a licensed grouper. Separate from
/// <see cref="Domain.GroupingStatus"/>, which mirrors MS-DRG return codes:
/// these say whether CHO got a usable answer at all, and the grouper's own
/// return code travels in <see cref="AprDrgGroupingResult.GrouperReturnCode"/>
/// so a new code in a vendor release needs no CHO change.
/// </summary>
public enum LicensedGroupingStatus
{
    /// <summary>The grouper assigned a group and the answer passed CHO's checks.</summary>
    Grouped,

    /// <summary>
    /// No licensed grouper is configured on this deployment. Pricing that
    /// needs one must pend, never fall back to a billed group.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The grouper answered and refused the claim: an invalid principal
    /// diagnosis, an ungroupable stay, an age or sex conflict. Its return code
    /// and messages say which.
    /// </summary>
    RejectedByGrouper,

    /// <summary>
    /// The grouper could not be reached, timed out or failed. A retry may
    /// succeed; the claim pends rather than paying anything.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The grouper answered with something CHO cannot trust: a malformed group,
    /// a severity outside 1 to 4, a line it was not asked about or a line
    /// missing. Treated as no answer, because pricing a half-understood
    /// response is how a wrong amount gets paid quietly.
    /// </summary>
    InvalidResponse,
}

// ─────────────────────────────────────────────────────────────────
//  APR-DRG (inpatient)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// One inpatient stay to classify by APR-DRG. Carries the inputs the 3M
/// APR-DRG grouper documents: diagnoses with present-on-admission,
/// ICD-10-PCS procedures, demographics, discharge status, dates and birth
/// weight.
/// </summary>
public sealed record AprDrgGroupingRequest
{
    /// <summary>
    /// CHO's own reference for the claim, echoed back so a grouper log can be
    /// matched to the claim. Carries no PHI.
    /// </summary>
    public required string ClaimReference { get; init; }

    /// <summary>The principal diagnosis (837I HI*ABK), without the decimal point.</summary>
    public required GroupingDiagnosis PrincipalDiagnosis { get; init; }

    /// <summary>Other diagnoses, in claim order.</summary>
    public IReadOnlyList<GroupingDiagnosis> SecondaryDiagnoses { get; init; } = [];

    /// <summary>
    /// The admitting diagnosis (837I HI*ABJ). APR-DRG reads it for some
    /// newborn and admission logic.
    /// </summary>
    public string? AdmittingDiagnosis { get; init; }

    /// <summary>ICD-10-PCS procedures, principal first.</summary>
    public IReadOnlyList<GroupingProcedure> Procedures { get; init; } = [];

    /// <summary>Admission date.</summary>
    public DateOnly? AdmissionDate { get; init; }

    /// <summary>Discharge date. Selects the grouper version when none is requested.</summary>
    public required DateOnly DischargeDate { get; init; }

    /// <summary>Age in whole years at admission.</summary>
    public int? AgeYears { get; init; }

    /// <summary>Age in days at admission; APR-DRG uses it for neonates.</summary>
    public int? AgeDays { get; init; }

    /// <summary>Administrative sex: M, F or U.</summary>
    public string? Sex { get; init; }

    /// <summary>UB-04 patient discharge status (FL 17).</summary>
    public string? DischargeStatus { get; init; }

    /// <summary>Birth weight in grams (837I value code 54), for neonates.</summary>
    public int? BirthWeightGrams { get; init; }

    /// <summary>Length of stay in days.</summary>
    public int? LengthOfStayDays { get; init; }

    /// <summary>
    /// The APR-DRG version the state prices this discharge under, e.g. "40".
    /// States adopt versions on their own calendars and not always the
    /// newest, so the pricing method chooses it from the state's table and
    /// the grouper must honour it. Null leaves the choice to the grouper.
    /// </summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The APR-DRG the facility billed, if any, for comparison only.</summary>
    public string? BilledDrg { get; init; }
}

/// <summary>
/// A licensed grouper's APR-DRG answer. The pair that prices a stay is the
/// base APR-DRG and the severity of illness; every state weight table is
/// keyed by both.
/// </summary>
public sealed record AprDrgGroupingResult
{
    /// <summary>Whether CHO got a usable answer.</summary>
    public required LicensedGroupingStatus Status { get; init; }

    /// <summary>The base APR-DRG, three digits (e.g. "720").</summary>
    public string? AprDrg { get; init; }

    /// <summary>Severity of illness, 1 (minor) to 4 (extreme).</summary>
    public int? SeverityOfIllness { get; init; }

    /// <summary>Risk of mortality, 1 to 4. Reported, not priced.</summary>
    public int? RiskOfMortality { get; init; }

    /// <summary>Major diagnostic category.</summary>
    public int? Mdc { get; init; }

    /// <summary>
    /// The group as state weight tables write it: the APR-DRG and severity
    /// joined with a hyphen, e.g. "720-3".
    /// </summary>
    public string? DrgSoiKey =>
        AprDrg is { } drg && SeverityOfIllness is { } soi ? $"{drg}-{soi}" : null;

    /// <summary>The grouper's own return code, verbatim.</summary>
    public string? GrouperReturnCode { get; init; }

    /// <summary>The version the grouper actually used, e.g. "40.0".</summary>
    public string? GrouperVersion { get; init; }

    /// <summary>Vendor and product that answered, e.g. "3M APR-DRG".</summary>
    public string? GrouperName { get; init; }

    /// <summary>The APR-DRG the facility billed, echoed from the request.</summary>
    public string? BilledDrg { get; init; }

    /// <summary>
    /// The grouper's messages and CHO's own notes on why an answer was
    /// refused, for the pend reason and the appeal record.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A result with no group, and why.</summary>
    public static AprDrgGroupingResult NotGrouped(
        LicensedGroupingStatus status, string? billedDrg, params string[] notes) => new()
    {
        Status = status,
        BilledDrg = billedDrg,
        Notes = notes,
    };
}

// ─────────────────────────────────────────────────────────────────
//  EAPG (outpatient)
// ─────────────────────────────────────────────────────────────────

/// <summary>One outpatient hospital claim line to classify.</summary>
public sealed record EapgLineInput
{
    /// <summary>The claim's line number; the answer must come back keyed by it.</summary>
    public required int LineNumber { get; init; }

    /// <summary>UB-04 revenue code.</summary>
    public string? RevenueCode { get; init; }

    /// <summary>HCPCS or CPT code, if the line has one.</summary>
    public string? ProcedureCode { get; init; }

    /// <summary>Procedure modifiers in claim order.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Units of service.</summary>
    public decimal Units { get; init; } = 1m;

    /// <summary>Line charge. EAPG uses it to pick among same-visit lines.</summary>
    public decimal ChargeAmount { get; init; }

    /// <summary>Line date of service.</summary>
    public required DateOnly ServiceDate { get; init; }
}

/// <summary>An outpatient hospital claim to classify by EAPG.</summary>
public sealed record EapgGroupingRequest
{
    /// <summary>CHO's own claim reference, echoed back. Carries no PHI.</summary>
    public required string ClaimReference { get; init; }

    /// <summary>The principal diagnosis, without the decimal point.</summary>
    public required string PrincipalDiagnosis { get; init; }

    /// <summary>Other diagnoses, in claim order.</summary>
    public IReadOnlyList<string> SecondaryDiagnoses { get; init; } = [];

    /// <summary>The patient's reason-for-visit diagnoses (837I HI*APR).</summary>
    public IReadOnlyList<string> ReasonForVisitDiagnoses { get; init; } = [];

    /// <summary>Type of bill, e.g. "0131".</summary>
    public string? TypeOfBill { get; init; }

    /// <summary>Statement from date.</summary>
    public required DateOnly FromDate { get; init; }

    /// <summary>Statement through date. Selects the version when none is requested.</summary>
    public required DateOnly ThroughDate { get; init; }

    /// <summary>Age in whole years on the from date.</summary>
    public int? AgeYears { get; init; }

    /// <summary>Administrative sex: M, F or U.</summary>
    public string? Sex { get; init; }

    /// <summary>UB-04 patient discharge status.</summary>
    public string? DischargeStatus { get; init; }

    /// <summary>The lines to classify. Every one must come back exactly once.</summary>
    public required IReadOnlyList<EapgLineInput> Lines { get; init; }

    /// <summary>The EAPG version the state prices this claim under. Null leaves it to the grouper.</summary>
    public string? RequestedVersion { get; init; }
}

/// <summary>
/// What EAPG decided a line is paid as. The payment method multiplies the
/// line's EAPG weight by the discount factor for <see cref="Payable"/> and
/// <see cref="Discounted"/> lines, and pays nothing separately for the rest.
/// </summary>
public enum EapgLineAction
{
    /// <summary>Paid at the full EAPG weight.</summary>
    Payable,

    /// <summary>Packaged into another line's payment on the same visit.</summary>
    Packaged,

    /// <summary>Consolidated with another significant procedure on the same visit.</summary>
    Consolidated,

    /// <summary>Paid at a reduced share of the weight (multiple procedure or bilateral discounting).</summary>
    Discounted,

    /// <summary>Not payable under EAPG (e.g. an inpatient-only procedure or an error EAPG).</summary>
    NotPayable,
}

/// <summary>The grouper's answer for one line.</summary>
public sealed record EapgLineResult
{
    /// <summary>The line number from the request.</summary>
    public required int LineNumber { get; init; }

    /// <summary>The assigned EAPG number, e.g. "00096".</summary>
    public string? Eapg { get; init; }

    /// <summary>
    /// The EAPG type the grouper reported (significant procedure, medical
    /// visit, ancillary, drug, per diem and so on), verbatim.
    /// </summary>
    public string? EapgType { get; init; }

    /// <summary>The EAPG category the grouper reported, verbatim.</summary>
    public string? EapgCategory { get; init; }

    /// <summary>How the line pays.</summary>
    public required EapgLineAction Action { get; init; }

    /// <summary>
    /// Share of the weight paid, between 0 and 1. 1 for a payable line,
    /// e.g. 0.5 for a second significant procedure, 0 for packaged lines.
    /// </summary>
    public decimal DiscountFactor { get; init; } = 1m;

    /// <summary>The visit the grouper assigned the line to, on a multi-day claim.</summary>
    public int? VisitNumber { get; init; }

    /// <summary>The grouper's edits or messages for this line.</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>A licensed grouper's EAPG answer for a whole claim.</summary>
public sealed record EapgGroupingResult
{
    /// <summary>Whether CHO got a usable answer.</summary>
    public required LicensedGroupingStatus Status { get; init; }

    /// <summary>One result per requested line when <see cref="Status"/> is Grouped.</summary>
    public IReadOnlyList<EapgLineResult> Lines { get; init; } = [];

    /// <summary>The grouper's claim-level return code, verbatim.</summary>
    public string? GrouperReturnCode { get; init; }

    /// <summary>The version the grouper used.</summary>
    public string? GrouperVersion { get; init; }

    /// <summary>Vendor and product that answered.</summary>
    public string? GrouperName { get; init; }

    /// <summary>The grouper's claim-level messages and CHO's notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A result with no grouping, and why.</summary>
    public static EapgGroupingResult NotGrouped(LicensedGroupingStatus status, params string[] notes) => new()
    {
        Status = status,
        Notes = notes,
    };
}
