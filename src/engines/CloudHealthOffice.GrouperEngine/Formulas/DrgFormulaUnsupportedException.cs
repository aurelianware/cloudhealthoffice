namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// A formula could not be evaluated because one of its operands is a test over
/// the claim's codes that this engine does not implement yet.
///
/// <para>
/// This is deliberately an exception rather than a false answer. These
/// operands — significant trauma, bilateral pairs, the fusion, vessel and
/// stent counts, MDC fall-through — are each the whole condition of the
/// formula they appear in, so answering false would silently move the stay to
/// a different DRG. There is no safe default, and a grouper that cannot
/// evaluate a formula has to refuse the claim rather than group it as though
/// the operand had failed.
/// </para>
/// </summary>
public sealed class DrgFormulaUnsupportedException : Exception
{
    /// <summary>Creates the exception for a named operand.</summary>
    public DrgFormulaUnsupportedException(string operand)
        : base($"The operand '{operand}' is a test over the claim's codes rather than its "
            + "mask, and is not implemented. Refuse the claim rather than grouping it as "
            + "though the operand had failed.")
    {
        Operand = operand;
    }

    /// <summary>The operand that could not be evaluated.</summary>
    public string Operand { get; }
}
