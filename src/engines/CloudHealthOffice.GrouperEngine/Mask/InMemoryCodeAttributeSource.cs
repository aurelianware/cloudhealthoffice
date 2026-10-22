using CloudHealthOffice.GrouperEngine.Domain;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// Code attributes held in memory, for tests and for a narrow set of codes an
/// operator wants to pin.
///
/// <para>
/// This is not the production path. CMS's V44 tables carry roughly 76,000
/// diagnosis rows and 89,000 procedure rows, and they are imported at run time
/// from a release the operator licences — see
/// <see cref="IMsDrgCodeAttributeSource"/>. What this type is for is stating a
/// handful of codes explicitly, so a test can say what a stay means without
/// needing CMS's tables present.
/// </para>
/// </summary>
public sealed class InMemoryCodeAttributeSource : IMsDrgCodeAttributeSource
{
    private readonly Dictionary<string, DiagnosisCodeAttributes> diagnoses;
    private readonly Dictionary<string, ProcedureCodeAttributes> procedures;

    /// <summary>Builds a source over the given codes.</summary>
    /// <param name="version">
    /// The CMS version these attributes were taken from. Recorded rather than
    /// checked: a mask built from one version's tables and evaluated against
    /// another's formulas is wrong in ways no assertion here would catch, so
    /// the version travels with the data and the caller is the one that must
    /// keep them together.
    /// </param>
    public InMemoryCodeAttributeSource(
        string version,
        IEnumerable<KeyValuePair<string, DiagnosisCodeAttributes>>? diagnoses = null,
        IEnumerable<KeyValuePair<string, ProcedureCodeAttributes>>? procedures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Version = version;

        // CMS compares code values case-sensitively but every table and every
        // claim path upper-cases them first, so an ordinal-ignore-case
        // dictionary here is the same lookup with one fewer way to go wrong.
        this.diagnoses = new Dictionary<string, DiagnosisCodeAttributes>(
            diagnoses ?? [], StringComparer.OrdinalIgnoreCase);
        this.procedures = new Dictionary<string, ProcedureCodeAttributes>(
            procedures ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public string Version { get; }

    /// <inheritdoc/>
    public bool TryGetDiagnosis(string code, out DiagnosisCodeAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(code);
        return diagnoses.TryGetValue(code, out attributes!);
    }

    /// <inheritdoc/>
    public bool TryGetProcedure(string code, out ProcedureCodeAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(code);
        return procedures.TryGetValue(code, out attributes!);
    }

    /// <summary>
    /// A diagnosis with only a diagnosis-category operand list, which is the
    /// shape two thirds of CMS's diagnosis rows have.
    /// </summary>
    public static DiagnosisCodeAttributes Diagnosis(
        int mdc,
        ComplicationLevel severity,
        params string[] diagnosisCategoryOperands) =>
        new(mdc, severity, [], diagnosisCategoryOperands, []);

    /// <summary>
    /// A diagnosis whose lists are operands rather than diagnosis categories.
    ///
    /// <para>
    /// The distinction matters for the code-driven operands, which read a
    /// code's operands and never its categories — as CMS's own diagnosis
    /// lookup does, its category branch being unreachable. The significant
    /// trauma lists arrive as operands in the real tables.
    /// </para>
    /// </summary>
    public static DiagnosisCodeAttributes DiagnosisWithOperands(
        int mdc,
        ComplicationLevel severity,
        params string[] operands) =>
        new(mdc, severity, operands, [], []);

    /// <summary>A procedure with the given operand list.</summary>
    public static ProcedureCodeAttributes Procedure(params string[] operands) =>
        new(operands);
}
