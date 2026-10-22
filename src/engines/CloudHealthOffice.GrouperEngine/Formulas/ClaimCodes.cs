namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// One diagnosis on the claim, as the code-driven operands see it.
///
/// <para>
/// Only <paramref name="Operands"/> is consulted, never the code's diagnosis
/// category lists. That is CMS's behaviour rather than a simplification:
/// <c>MsdrgDiagnosisCode.hasAttribute(String)</c> reads the category set only
/// when the operand set is <see langword="null"/>, and the field is
/// initialised to an empty set, so the category branch is unreachable. It is
/// also moot for the lists that matter here — every V44 code carrying a
/// significant-trauma list carries it as an operand, 2,767 of them, and none
/// carries one only as a category.
/// </para>
/// </summary>
/// <param name="Code">The diagnosis code, for explaining a decision.</param>
/// <param name="Mdc">The MDC the code is assigned to.</param>
/// <param name="Operands">
/// The code's operand list names, compared case-insensitively and without a
/// position prefix, as CMS's string overload does.
/// </param>
public sealed record ClaimDiagnosis(
    string Code,
    int Mdc,
    IReadOnlySet<string> Operands)
{
    /// <summary>
    /// True when the code is on the named list.
    ///
    /// <para>
    /// A walk rather than a set lookup, because CMS's is: it compares list
    /// names with <c>equalsIgnoreCase</c>, so the answer must not depend on
    /// which comparer the operand set happens to have been built with.
    /// </para>
    /// </summary>
    public bool HasOperand(string listName)
    {
        foreach (var operand in Operands)
        {
            if (string.Equals(operand, listName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a diagnosis with the right comparer for its operand set.
    /// </summary>
    public static ClaimDiagnosis Create(
        string code, int mdc, IEnumerable<string> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operand in operands)
        {
            // The tables' operand strings may carry a position prefix; CMS
            // compares list names only.
            names.Add(OperandName.Parse(operand).ListName);
        }

        return new ClaimDiagnosis(code, mdc, names);
    }
}

/// <summary>
/// One procedure on the claim, as the code-driven operands see it.
///
/// <para>
/// A procedure's attributes are matched by whole attribute — prefix and list
/// name, case-sensitively — because CMS's
/// <c>MsdrgProcedureCode.hasAttribute</c> is a hash-set lookup rather than the
/// list walk the diagnosis side uses. The difference is visible: CMS says a
/// procedure carrying <c>RIGHTHIP</c> does not satisfy <c>BILATERAL</c>, while
/// a diagnosis carrying <c>STHEAD</c> does satisfy <c>MULTST</c>.
/// </para>
/// </summary>
/// <param name="Code">The procedure code, for explaining a decision.</param>
/// <param name="Operands">The code's attributes, prefix included.</param>
public sealed record ClaimProcedure(string Code, IReadOnlySet<OperandName> Operands)
{
    /// <summary>True when the code carries the unprefixed named list.</summary>
    public bool HasOperand(string listName) =>
        Operands.Contains(new OperandName(OperandPosition.Unqualified, listName));

    /// <summary>Builds a procedure from the tables' operand strings.</summary>
    public static ClaimProcedure Create(string code, IEnumerable<string> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);

        var names = new HashSet<OperandName>();
        foreach (var operand in operands)
        {
            names.Add(OperandName.Parse(operand));
        }

        return new ClaimProcedure(code, names);
    }
}

/// <summary>
/// The claim's codes in the order they were submitted, which the code-driven
/// operands walk.
///
/// <para>
/// The order is load-bearing for two of them. <c>MDC_20_FALL_THRU</c> reads
/// the principal diagnosis and nothing else, so it depends on the principal
/// being first; and the counting nodes stop once they have their answer, which
/// decides which codes get marked as having affected the DRG even when it does
/// not change the answer itself.
/// </para>
/// </summary>
/// <param name="Diagnoses">
/// The principal diagnosis first, then the secondaries in submitted order.
/// </param>
/// <param name="Procedures">The procedures in submitted order.</param>
public sealed record ClaimCodes(
    IReadOnlyList<ClaimDiagnosis> Diagnoses,
    IReadOnlyList<ClaimProcedure> Procedures)
{
    /// <summary>
    /// No codes. Every code-driven operand is false against it, which is what
    /// CMS answers for a claim with no codes.
    /// </summary>
    public static ClaimCodes Empty { get; } = new([], []);
}
