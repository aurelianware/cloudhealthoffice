using CloudHealthOffice.GrouperEngine.Formulas;

namespace CloudHealthOffice.GrouperEngine.Hac;

/// <summary>
/// UB-04 present-on-admission, as CMS reads it (field 67 A-Q).
///
/// <para>
/// Four of these eight values — <see cref="ExemptOne"/>, <see cref="ExemptE"/>,
/// <see cref="Blank"/> and <see cref="Invalid"/> — behave identically in every
/// rule CMS applies: each means the indicator cannot be read, and on a claim
/// carrying a hospital-acquired condition each makes the claim ungroupable.
/// They are kept apart anyway, because the distinction is what a POA error
/// report is made of and collapsing them now would make that report
/// unbuildable later.
/// </para>
/// </summary>
public enum PresentOnAdmission
{
    /// <summary>Present on admission.</summary>
    Yes,

    /// <summary>Not present on admission.</summary>
    No,

    /// <summary>Documentation insufficient to determine.</summary>
    Unknown,

    /// <summary>Clinically undetermined at the time of admission.</summary>
    ClinicallyUndetermined,

    /// <summary>Exempt from POA reporting, written <c>1</c>.</summary>
    ExemptOne,

    /// <summary>Exempt from POA reporting, written <c>E</c>.</summary>
    ExemptE,

    /// <summary>Nothing reported.</summary>
    Blank,

    /// <summary>Something was reported that is not a POA indicator.</summary>
    Invalid,
}

/// <summary>Whether the facility reports POA at all.</summary>
public enum HospitalPoaReporting
{
    /// <summary>Reports POA. The HAC policy applies in full.</summary>
    NonExempt,

    /// <summary>
    /// Exempt from POA reporting. No condition is treated as
    /// hospital-acquired, so nothing is taken out of the severity count.
    /// </summary>
    Exempt,

    /// <summary>
    /// Not known whether the facility reports POA. CMS refuses the claim
    /// rather than guessing — see <see cref="MsDrgHacProcessor"/>, where the
    /// refusal is stricter than it looks.
    /// </summary>
    Unknown,
}

/// <summary>What the HAC policy decided about one condition on one code.</summary>
public enum MsDrgHacStatus
{
    /// <summary>The code is on no HAC list.</summary>
    NotOnHacList,

    /// <summary>
    /// The condition was acquired in hospital. This is the one value that
    /// changes payment: the code stops counting towards severity.
    /// </summary>
    CriteriaMet,

    /// <summary>
    /// The code is on the list but the condition was not acquired in hospital
    /// — present on admission, or the HAC's own formula was not satisfied.
    /// </summary>
    CriteriaNotMet,

    /// <summary>
    /// The code was already excluded from the severity count by the principal
    /// diagnosis's CC exclusion list, so the HAC rules have nothing to do.
    /// </summary>
    NotApplicableExclusion,

    /// <summary>The facility is exempt from POA reporting.</summary>
    NotApplicableExempt,
}

/// <summary>
/// One of CMS's 14 hospital-acquired conditions as a code carries it: the
/// condition's number, and the operand list that put this code on it.
///
/// <para>
/// Both halves matter. The number selects the formula to evaluate; the list
/// name is what HAC 6's show-list rule turns on, and a code can be on HAC 6
/// through either <c>hac06_sdx</c> or <c>hac06_show</c>.
/// </para>
/// </summary>
/// <param name="Number">CMS's HAC number, 1 to 14.</param>
/// <param name="ListName">The operand list this code is on.</param>
public sealed record MsDrgHacEntry(int Number, string ListName);

/// <summary>
/// CMS's HAC tables for one grouper version: which codes are on which HAC
/// lists, and the formula that decides each condition.
/// </summary>
public interface IMsDrgHacSource
{
    /// <summary>The grouper version these tables came from.</summary>
    string Version { get; }

    /// <summary>
    /// The HAC lists a code is on, or empty for a code on none. A procedure
    /// code answers here too: six of the conditions need one.
    /// </summary>
    IReadOnlyList<MsDrgHacEntry> EntriesFor(string code);

    /// <summary>
    /// A condition's formulas, in table order. The first that matches decides
    /// it. V44 gives each of the 14 exactly one.
    /// </summary>
    IReadOnlyList<DrgFormulaExpression> FormulasFor(int hacNumber);

    /// <summary>The HAC numbers the tables hold a formula for.</summary>
    IReadOnlyCollection<int> HacNumbers { get; }
}

/// <summary>
/// An <see cref="IMsDrgHacSource"/> over tables already in memory.
/// </summary>
public sealed class InMemoryHacSource : IMsDrgHacSource
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MsDrgHacEntry>> byCode;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<DrgFormulaExpression>> byNumber;

    /// <summary>Creates a source.</summary>
    public InMemoryHacSource(
        string version,
        IReadOnlyDictionary<string, IReadOnlyList<MsDrgHacEntry>> entriesByCode,
        IReadOnlyDictionary<int, IReadOnlyList<DrgFormulaExpression>> formulasByHacNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(entriesByCode);
        ArgumentNullException.ThrowIfNull(formulasByHacNumber);

        Version = version;
        byCode = entriesByCode;
        byNumber = formulasByHacNumber;
    }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<int> HacNumbers => (IReadOnlyCollection<int>)byNumber.Keys;

    /// <summary>How many codes are on at least one HAC list.</summary>
    public int CodeCount => byCode.Count;

    /// <inheritdoc />
    public IReadOnlyList<MsDrgHacEntry> EntriesFor(string code) =>
        byCode.TryGetValue(code, out var entries) ? entries : [];

    /// <inheritdoc />
    public IReadOnlyList<DrgFormulaExpression> FormulasFor(int hacNumber) =>
        byNumber.TryGetValue(hacNumber, out var formulas) ? formulas : [];

    /// <summary>
    /// A source with no conditions in it, for a caller that has deliberately
    /// decided the HAC policy does not apply. Not a default: an empty set of
    /// hospital-acquired conditions pays more than the claim has earned.
    /// </summary>
    public static IMsDrgHacSource None { get; } =
        new InMemoryHacSource(
            "none",
            new Dictionary<string, IReadOnlyList<MsDrgHacEntry>>(),
            new Dictionary<int, IReadOnlyList<DrgFormulaExpression>>());
}
