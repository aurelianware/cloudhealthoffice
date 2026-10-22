namespace CloudHealthOffice.GrouperEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// GROUPER DOMAIN TYPES
//
// The concepts an inpatient or outpatient facility claim is grouped by
// before it can be priced. See ADR 024.
//
//   MS-DRG: MDC → partition (medical/surgical) → base family → severity
//   APC:    HCPCS → status indicator → APC, then packaging across lines
//
// QNXT / Facets equivalents:
//   MsDrgGroupingResult → the DRG assignment row written by the grouper
//                         interface (QNXT CLAIM.DRG_CODE + DRG_SOURCE)
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Which half of an MDC a stay falls in. CMS splits every MDC into a
/// surgical partition (the stay has an operating-room procedure) and a
/// medical partition (it does not).
/// </summary>
public enum DrgPartition
{
    /// <summary>Assigned before MDC assignment: transplants, tracheostomy, ECMO.</summary>
    PreMdc,

    /// <summary>No operating-room procedure on the stay.</summary>
    Medical,

    /// <summary>At least one operating-room procedure on the stay.</summary>
    Surgical
}

/// <summary>
/// The severity split within a base DRG family. A family has one, two or
/// three members; <see cref="Unsplit"/> is the single-member case
/// (e.g. MS-DRG 470 has no severity sibling in its own right).
/// </summary>
public enum DrgSeverity
{
    /// <summary>The family is not split by severity.</summary>
    Unsplit,

    /// <summary>"without CC/MCC".</summary>
    WithoutCcMcc,

    /// <summary>"with CC".</summary>
    WithCc,

    /// <summary>"with MCC".</summary>
    WithMcc
}

/// <summary>
/// How much a secondary diagnosis raises severity. Determined by the CMS
/// CC/MCC lists, after the CC exclusion list has been applied against the
/// principal diagnosis.
/// </summary>
public enum ComplicationLevel
{
    /// <summary>Neither a CC nor an MCC, or excluded by the principal diagnosis.</summary>
    None,

    /// <summary>Complication or comorbidity.</summary>
    Cc,

    /// <summary>Major complication or comorbidity.</summary>
    Mcc
}

/// <summary>
/// The outcome of a grouping attempt. Mirrors the CMS grouper return codes
/// closely enough to be auditable without claiming to be them.
/// </summary>
public enum GroupingStatus
{
    /// <summary>A DRG was assigned.</summary>
    Grouped,

    /// <summary>No principal diagnosis on the claim.</summary>
    MissingPrincipalDiagnosis,

    /// <summary>The principal diagnosis is not in the grouper's code set for this version.</summary>
    InvalidPrincipalDiagnosis,

    /// <summary>
    /// The principal diagnosis is a valid ICD-10 code but may not stand as a
    /// principal diagnosis (MCE "unacceptable principal diagnosis").
    /// </summary>
    UnacceptablePrincipalDiagnosis,

    /// <summary>The principal diagnosis maps to no MDC in this version's tables.</summary>
    NoMdcAssignment,

    /// <summary>An MDC was assigned but no DRG family rule matched the stay.</summary>
    NoDrgRuleMatched,

    /// <summary>
    /// Age, sex or discharge status conflicts with the assigned group
    /// (MCE age/sex/discharge-status edits).
    /// </summary>
    DemographicConflict,

    /// <summary>The grouper has no tables loaded for the stay's discharge date.</summary>
    NoTablesForDischargeDate,

    /// <summary>
    /// A Medicare Code Editor edit in the grouper's own tables refused the
    /// claim. The edit's return code is carried in the result's notes, because
    /// the edits are table data and a new fiscal year can add one.
    /// </summary>
    RefusedByCodeEdit,

    /// <summary>
    /// The present-on-admission indicators cannot carry the
    /// hospital-acquired-condition decision, so there is no lawful DRG to pay:
    /// one is missing, or one is unreadable on a stay with a condition whose
    /// status would change the payment. CMS refuses the claim here rather than
    /// guessing, and so does this.
    /// </summary>
    PresentOnAdmissionUnusable,

    /// <summary>
    /// The grouper was asked for a DRG it was not given one to report: the
    /// billed-DRG implementation with no DRG on the claim. Not a grouping
    /// failure — nothing was grouped.
    /// </summary>
    NoBilledDrg
}

/// <summary>Where a paid DRG came from — the audit question a payer must answer.</summary>
public enum DrgSource
{
    /// <summary>The DRG the facility billed in 837I HI*DR. No grouping was done.</summary>
    BilledByProvider,

    /// <summary>Assigned by the platform's own table-driven grouper.</summary>
    ChoGrouper,

    /// <summary>Assigned by the CMS reference grouper running as a sidecar.</summary>
    CmsReferenceGrouper,

    /// <summary>Assigned by a licensed third-party grouper (3M/Solventum, Optum).</summary>
    LicensedGrouper,

    /// <summary>Set by a human on review or appeal.</summary>
    ManualOverride
}

/// <summary>OPPS payment status indicator (OPPS Addendum B, column SI).</summary>
public static class OppsStatusIndicator
{
    /// <summary>Paid under a fee schedule other than OPPS.</summary>
    public const string NotPaidUnderOpps = "A";

    /// <summary>Packaged into the APC of another line; no separate payment.</summary>
    public const string PackagedService = "N";

    /// <summary>Separately payable procedure, subject to multiple-procedure discounting.</summary>
    public const string SignificantProcedureDiscounted = "T";

    /// <summary>Separately payable procedure, not discounted.</summary>
    public const string SignificantProcedure = "S";

    /// <summary>Clinic or emergency department visit.</summary>
    public const string Visit = "V";

    /// <summary>STVX-packaged: paid separately only with no S, T, V or X line.</summary>
    public const string ConditionallyPackagedStvx = "Q1";

    /// <summary>T-packaged: paid separately only with no T line on the same date.</summary>
    public const string ConditionallyPackagedT = "Q2";

    /// <summary>May be paid through a composite APC.</summary>
    public const string ConditionallyPackagedComposite = "Q3";

    /// <summary>Comprehensive APC primary service; everything else on the claim packages into it.</summary>
    public const string ComprehensiveApcPrimary = "J1";

    /// <summary>Hospital part-B services on a claim with no J1 primary.</summary>
    public const string ComprehensiveApcSecondary = "J2";

    /// <summary>Not payable by Medicare.</summary>
    public const string NotPayable = "E1";
}
