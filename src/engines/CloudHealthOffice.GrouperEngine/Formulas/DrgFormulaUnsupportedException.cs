namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// A formula could not be evaluated because one of its operands is a special
/// logic list this engine has no rule for.
///
/// <para>
/// Every list in <see cref="SpecialLogicList.Known"/> has a rule today, so
/// nothing raises this on a V44 claim. It is the guard for the next fiscal
/// year: CMS adds a special list, the table gains the name without a rule, and
/// the formulas that use it have to stop rather than answer false.
/// </para>
///
/// <para>
/// Refusing is deliberate. These operands — significant trauma, bilateral
/// pairs, the fusion, vessel and stent counts, MDC fall-through — are each the
/// whole condition of the formula they appear in, so answering false would
/// silently move the stay to a different DRG. There is no safe default, and a
/// grouper that cannot evaluate a formula has to refuse the claim rather than
/// group it as though the operand had failed.
/// </para>
/// </summary>
public sealed class DrgFormulaUnsupportedException : Exception
{
    /// <summary>Creates the exception for a named operand.</summary>
    public DrgFormulaUnsupportedException(string operand)
        : base($"The operand '{operand}' is a special logic list with no rule in this "
            + "engine. Refuse the claim rather than grouping it as though the operand "
            + "had failed.")
    {
        Operand = operand;
    }

    /// <summary>The operand that could not be evaluated.</summary>
    public string Operand { get; }
}
