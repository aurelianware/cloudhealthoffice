using CloudHealthOffice.GrouperEngine.Domain;

namespace CloudHealthOffice.GrouperEngine.Models;

/// <summary>
/// One MS-DRG as the CMS definitions manual defines it. The grouping logic
/// never hard-codes a DRG number; it resolves a family and a severity and
/// looks the pair up here.
/// </summary>
public sealed record MsDrgDefinition
{
    /// <summary>MS-DRG code, 3 characters, zero-padded (e.g. <c>470</c>, <c>003</c>).</summary>
    public required string Drg { get; init; }

    /// <summary>Official title (definitions manual).</summary>
    public required string Title { get; init; }

    /// <summary>Major diagnostic category; null for a pre-MDC DRG (001-017).</summary>
    public int? Mdc { get; init; }

    /// <summary>Which partition this DRG belongs to.</summary>
    public required DrgPartition Partition { get; init; }

    /// <summary>
    /// The base family this DRG is a severity member of. Members of a family
    /// differ only by severity (e.g. <c>HEART_FAILURE</c> → 291/292/293).
    /// </summary>
    public required string FamilyKey { get; init; }

    /// <summary>This DRG's position in its family's severity split.</summary>
    public required DrgSeverity Severity { get; init; }

    /// <summary>
    /// Sex this DRG requires, when it is sex-specific (<c>M</c> or <c>F</c>).
    /// Null when the DRG is not sex-specific.
    /// </summary>
    public string? RequiredSex { get; init; }
}

/// <summary>Maps a principal diagnosis to its major diagnostic category.</summary>
/// <param name="Code">ICD-10-CM code, no decimal point.</param>
/// <param name="Mdc">The MDC the code assigns to.</param>
/// <param name="AcceptableAsPrincipal">
/// False for an MCE "unacceptable principal diagnosis" — a valid code that
/// cannot stand alone as the reason for admission.
/// </param>
public sealed record MdcAssignment(string Code, int Mdc, bool AcceptableAsPrincipal = true);

/// <summary>
/// A secondary diagnosis's place on the CC or MCC list.
/// </summary>
/// <param name="Code">ICD-10-CM code, no decimal point.</param>
/// <param name="Level">CC or MCC.</param>
public sealed record ComplicationAssignment(string Code, ComplicationLevel Level);

/// <summary>
/// One row of the CC exclusion list: when the principal diagnosis is
/// <paramref name="PrincipalCode"/>, the secondary codes in
/// <paramref name="ExcludedSecondaryCodes"/> do not count as a CC or MCC,
/// because they are part of the same clinical picture.
/// </summary>
public sealed record CcExclusion(string PrincipalCode, IReadOnlySet<string> ExcludedSecondaryCodes);

/// <summary>
/// An operating-room procedure and the surgical class it belongs to within
/// an MDC. A stay with several OR procedures groups to the class with the
/// lowest <paramref name="HierarchyRank"/> — the CMS surgical hierarchy.
/// </summary>
/// <param name="Code">ICD-10-PCS code.</param>
/// <param name="Mdc">The MDC this class belongs to.</param>
/// <param name="SurgicalClass">Class key, e.g. <c>MAJOR_JOINT_LOWER</c>.</param>
/// <param name="HierarchyRank">1 is highest. Ties fall back to claim order.</param>
public sealed record SurgicalClassAssignment(string Code, int Mdc, string SurgicalClass, int HierarchyRank);

/// <summary>
/// A rule that selects a base DRG family once the MDC and partition are
/// known. Rules are evaluated in <see cref="Rank"/> order and the first
/// match wins, which is how the definitions manual reads.
/// </summary>
public sealed record DrgFamilyRule
{
    /// <summary>Evaluation order within the MDC and partition. 1 is first.</summary>
    public required int Rank { get; init; }

    /// <summary>The MDC this rule applies within; null for a pre-MDC rule.</summary>
    public int? Mdc { get; init; }

    /// <summary>The partition this rule applies to.</summary>
    public required DrgPartition Partition { get; init; }

    /// <summary>The family assigned when the rule matches.</summary>
    public required string FamilyKey { get; init; }

    /// <summary>
    /// Required surgical class. Set on surgical rules; null on medical rules.
    /// </summary>
    public string? SurgicalClass { get; init; }

    /// <summary>
    /// Principal diagnoses this rule is limited to. Empty means the rule does
    /// not test the principal diagnosis.
    /// </summary>
    public IReadOnlySet<string> PrincipalDiagnoses { get; init; } = new HashSet<string>();

    /// <summary>
    /// Procedures that must all be present (pre-MDC rules: transplant plus
    /// the organ procedure). Empty means the rule does not test procedures
    /// beyond <see cref="SurgicalClass"/>.
    /// </summary>
    public IReadOnlySet<string> RequiredProcedures { get; init; } = new HashSet<string>();

    /// <summary>
    /// Discharge statuses this rule is limited to (e.g. <c>20</c> for the
    /// died-in-hospital families). Empty means any.
    /// </summary>
    public IReadOnlySet<string> DischargeStatuses { get; init; } = new HashSet<string>();
}

/// <summary>
/// The whole table set for one grouper version, loaded as a unit. A stay is
/// grouped by the set whose effective window contains its discharge date.
/// </summary>
public sealed record MsDrgTableSet
{
    /// <summary>CMS version label, e.g. <c>V44</c>.</summary>
    public required string Version { get; init; }

    /// <summary>First discharge date this version applies to (inclusive).</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Last discharge date this version applies to (inclusive); null while current.</summary>
    public DateOnly? EffectiveThrough { get; init; }

    /// <summary>Every DRG in the version, keyed by code.</summary>
    public required IReadOnlyDictionary<string, MsDrgDefinition> Drgs { get; init; }

    /// <summary>Principal-diagnosis to MDC assignment, keyed by code.</summary>
    public required IReadOnlyDictionary<string, MdcAssignment> MdcAssignments { get; init; }

    /// <summary>The CC and MCC lists, keyed by code.</summary>
    public required IReadOnlyDictionary<string, ComplicationAssignment> Complications { get; init; }

    /// <summary>The CC exclusion list, keyed by principal diagnosis.</summary>
    public required IReadOnlyDictionary<string, CcExclusion> CcExclusions { get; init; }

    /// <summary>
    /// Operating-room procedures. A procedure can belong to a class in more
    /// than one MDC, so the key is the code and the value is every assignment
    /// for it.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<SurgicalClassAssignment>> SurgicalClasses { get; init; }

    /// <summary>Family selection rules, in no particular order; the grouper sorts by rank.</summary>
    public required IReadOnlyList<DrgFamilyRule> FamilyRules { get; init; }

    /// <summary>
    /// Diagnoses the version knows at all. Used only to tell "not a valid
    /// code" from "valid but maps to no MDC". When empty, the grouper treats
    /// the MDC assignment table as the code set.
    /// </summary>
    public IReadOnlySet<string> KnownDiagnoses { get; init; } = new HashSet<string>();

    /// <summary>True when <paramref name="dischargeDate"/> falls in this version's window.</summary>
    public bool Covers(DateOnly dischargeDate) =>
        dischargeDate >= EffectiveFrom && (EffectiveThrough is null || dischargeDate <= EffectiveThrough);
}
