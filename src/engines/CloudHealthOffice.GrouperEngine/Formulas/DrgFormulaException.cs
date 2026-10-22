namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// A DRG logic formula could not be parsed.
///
/// <para>
/// This is always a data problem, not a claim problem: the formulas come from
/// the imported CMS table set, so a failure here means the import is wrong or
/// truncated. It must not be caught and turned into a "did not group" result,
/// because that would pay claims against a table set we cannot read.
/// </para>
/// </summary>
public sealed class DrgFormulaException : Exception
{
    /// <summary>The formula that could not be parsed.</summary>
    public string Formula { get; }

    /// <summary>Where in the formula the parser gave up, or -1 if not known.</summary>
    public int Position { get; }

    /// <summary>Creates the exception.</summary>
    public DrgFormulaException(string formula, int position, string message)
        : base($"{message} (formula \"{formula}\", position {position})")
    {
        Formula = formula;
        Position = position;
    }
}
