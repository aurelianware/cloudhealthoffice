using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Hac;

namespace CloudHealthOffice.GrouperEngine.Models;

/// <summary>
/// A diagnosis as the grouper sees it. Built from <c>Claim.DiagnosisCodes</c>:
/// the ICD-10-CM code with its decimal point removed (the grouper tables are
/// keyed without it) and the present-on-admission indicator.
/// </summary>
/// <param name="Code">ICD-10-CM code, no decimal point, upper case (e.g. <c>E1165</c>).</param>
/// <param name="PresentOnAdmission">
/// 837I HI0x-9: Y, N, U, W, or 1 (exempt). Hospital-acquired conditions
/// (<c>N</c> or <c>U</c>) do not raise severity under the HAC payment policy.
/// </param>
public sealed record GroupingDiagnosis(string Code, string? PresentOnAdmission = null);

/// <summary>An ICD-10-PCS procedure on the stay, no decimal point, upper case.</summary>
/// <param name="Code">ICD-10-PCS code (7 characters, e.g. <c>0SR9019</c>).</param>
/// <param name="Date">Procedure date, when the claim carried one.</param>
public sealed record GroupingProcedure(string Code, DateOnly? Date = null);

/// <summary>
/// Everything an MS-DRG grouper needs from an inpatient stay. Deliberately
/// decoupled from <c>claims-service</c>'s <c>Claim</c> so the engine has no
/// dependency on the claim model; the adapter that builds this lives in the
/// calling service.
/// </summary>
public sealed record MsDrgGroupingRequest
{
    /// <summary>Principal diagnosis (837I HI*ABK). Required.</summary>
    public required GroupingDiagnosis PrincipalDiagnosis { get; init; }

    /// <summary>Secondary diagnoses (837I HI*ABF), in claim order.</summary>
    public IReadOnlyList<GroupingDiagnosis> SecondaryDiagnoses { get; init; } = [];

    /// <summary>
    /// ICD-10-PCS procedures, principal first (837I HI*BBR then HI*BBQ).
    /// The surgical hierarchy is applied across all of them, not just the
    /// principal one.
    /// </summary>
    public IReadOnlyList<GroupingProcedure> Procedures { get; init; } = [];

    /// <summary>
    /// Discharge date. Selects the grouper version: a stay is grouped by the
    /// tables in force on the date of discharge, not the date of admission.
    /// </summary>
    public required DateOnly DischargeDate { get; init; }

    /// <summary>Patient age in years at admission. Drives the MCE age edits.</summary>
    public int? AgeYears { get; init; }

    /// <summary>
    /// Age in days at admission, for neonates. Required to tell a normal
    /// newborn (MDC 15) from a stay grouped on age in years.
    /// </summary>
    public int? AgeDays { get; init; }

    /// <summary>Administrative sex: <c>M</c>, <c>F</c> or <c>U</c>. Drives the MCE sex edits.</summary>
    public string? Sex { get; init; }

    /// <summary>
    /// Whether the facility reports present-on-admission indicators. CMS takes
    /// this as a runtime option rather than off the claim, because it is a
    /// property of the provider.
    ///
    /// <para>
    /// There is no safe default. A facility wrongly treated as exempt is paid
    /// for harm it caused; one wrongly treated as reporting has its claim
    /// refused. <see cref="HospitalPoaReporting.Unknown"/> is CMS's own answer
    /// to that and refuses the claim, so it is the default here too.
    /// </para>
    /// </summary>
    public HospitalPoaReporting PoaReporting { get; init; } = HospitalPoaReporting.Unknown;

    /// <summary>UB-04 patient (discharge) status, 837I CL103. <c>02</c>, <c>20</c>, <c>07</c> change grouping.</summary>
    public string? DischargeStatus { get; init; }

    /// <summary>Length of stay in days, from <c>CalculateLengthOfStay()</c>.</summary>
    public int? LengthOfStayDays { get; init; }

    /// <summary>Birth weight in grams (837I value code 54), for MDC 15 grouping.</summary>
    public int? BirthWeightGrams { get; init; }

    /// <summary>
    /// The DRG the facility billed, carried through only so the result can
    /// report agreement or disagreement. Never used to group.
    /// </summary>
    public string? BilledDrg { get; init; }
}

/// <summary>
/// A grouping result, written to be auditable: every input that moved the
/// answer is named, so an appeal can be answered from the record alone.
/// </summary>
public sealed record MsDrgGroupingResult
{
    /// <summary>Whether a DRG was assigned, and if not, why not.</summary>
    public required GroupingStatus Status { get; init; }

    /// <summary>The assigned MS-DRG (3 characters), or null when <see cref="Status"/> is not Grouped.</summary>
    public string? Drg { get; init; }

    /// <summary>The assigned DRG's title, for display and the remittance.</summary>
    public string? DrgTitle { get; init; }

    /// <summary>Major diagnostic category, or null for a pre-MDC assignment.</summary>
    public int? Mdc { get; init; }

    /// <summary>Which partition of the MDC the stay fell in.</summary>
    public DrgPartition? Partition { get; init; }

    /// <summary>The base DRG family the severity split was taken from.</summary>
    public string? FamilyKey { get; init; }

    /// <summary>The severity the family was resolved at.</summary>
    public DrgSeverity? Severity { get; init; }

    /// <summary>
    /// The highest complication level found among the secondary diagnoses,
    /// after CC exclusions and the HAC present-on-admission rule.
    /// </summary>
    public ComplicationLevel ComplicationLevel { get; init; }

    /// <summary>
    /// The secondary diagnosis that set <see cref="ComplicationLevel"/>. The
    /// one field an appeal is most often about.
    /// </summary>
    public string? DrivingComplicationCode { get; init; }

    /// <summary>
    /// Secondary diagnoses that are on the CC or MCC list but were not
    /// counted, each with the reason (CC exclusion against the principal
    /// diagnosis, or not present on admission).
    /// </summary>
    public IReadOnlyList<ExcludedComplication> ExcludedComplications { get; init; } = [];

    /// <summary>
    /// The surgical class that selected the family, when the stay grouped
    /// surgically. Null for a medical or pre-MDC assignment.
    /// </summary>
    public string? SurgicalClass { get; init; }

    /// <summary>
    /// The operating-room procedure that put the stay in the surgical
    /// partition, after the surgical hierarchy picked the highest-ranked one.
    /// </summary>
    public string? DrivingProcedureCode { get; init; }

    /// <summary>Which grouper produced this.</summary>
    public required DrgSource Source { get; init; }

    /// <summary>The grouper table version used, e.g. <c>V44</c>.</summary>
    public string? GrouperVersion { get; init; }

    /// <summary>The DRG the facility billed, when the claim carried one.</summary>
    public string? BilledDrg { get; init; }

    /// <summary>
    /// True when the facility billed a DRG and it differs from the assigned
    /// one. The payment-integrity signal; null when nothing was billed.
    /// </summary>
    public bool? DisagreesWithBilled =>
        BilledDrg is null or "" || Drg is null
            ? null
            : !string.Equals(BilledDrg.TrimStart('0'), Drg.TrimStart('0'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable notes: edits applied, rules skipped, assumptions made.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A result that assigned nothing, with the reason.</summary>
    public static MsDrgGroupingResult NotGrouped(
        GroupingStatus status, string version, string? billedDrg, params string[] notes) => new()
    {
        Status = status,
        Source = DrgSource.ChoGrouper,
        GrouperVersion = version,
        BilledDrg = billedDrg,
        Notes = notes
    };
}

/// <summary>A CC or MCC that did not count, and why.</summary>
/// <param name="Code">The secondary diagnosis.</param>
/// <param name="Level">The level it would have contributed.</param>
/// <param name="Reason">Why it was not counted.</param>
public sealed record ExcludedComplication(string Code, ComplicationLevel Level, string Reason);
