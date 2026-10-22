namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>The kinds of token a DRG logic formula is made of.</summary>
public enum DrgFormulaTokenKind
{
    /// <summary>
    /// An operand name: anything that is not one of the punctuation characters
    /// below. In CMS's tables these are list names such as <c>CC</c>,
    /// <c>backneck</c>, <c>fuse9plus</c> or <c>NORcardcath</c>.
    /// </summary>
    Operand,

    /// <summary>Left parenthesis.</summary>
    LeftParenthesis,

    /// <summary>Right parenthesis.</summary>
    RightParenthesis,

    /// <summary>Logical AND, written <c>&amp;</c>.</summary>
    And,

    /// <summary>Logical OR, written <c>|</c>.</summary>
    Or,

    /// <summary>Logical NOT, written <c>~</c>.</summary>
    Not,
}

/// <summary>One token, with the source text it came from.</summary>
/// <param name="Kind">What kind of token this is.</param>
/// <param name="Text">
/// The operand name for <see cref="DrgFormulaTokenKind.Operand"/>, otherwise
/// the punctuation character itself.
/// </param>
/// <param name="Position">
/// Where in the formula the token starts, so a parse error can point at it.
/// </param>
public readonly record struct DrgFormulaToken(
    DrgFormulaTokenKind Kind,
    string Text,
    int Position);
