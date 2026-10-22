namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// Parses a DRG logic formula into a <see cref="DrgFormulaExpression"/>.
///
/// <para>
/// A recursive-descent parser over the six-token language, with CMS's own
/// precedence: <c>~</c> binds tightest, then <c>&amp;</c>, then <c>|</c>. CMS
/// writes that as operator precedence 1 for AND and 2 for OR — a higher number
/// meaning looser — and treats NOT as a prefix on a factor rather than as a
/// ranked operator. Both operators are left-associative.
/// </para>
///
/// <para>
/// So <c>a | b &amp; c</c> parses as <c>a | (b &amp; c)</c>, and
/// <c>~a &amp; b</c> as <c>(~a) &amp; b</c>. CMS's own formulas are mostly
/// parenthesised — <c>"(sensor | NORcardcath) &amp; bypass"</c> — but not
/// always: <c>"fuse9plus | cmadifd | tulip"</c> has none, so the precedence
/// rules are load-bearing and are asserted directly in the tests.
/// </para>
/// </summary>
public static class DrgFormulaParser
{
    /// <summary>Parses <paramref name="formula"/>.</summary>
    /// <exception cref="DrgFormulaException">
    /// The formula is malformed. This means the imported table set is wrong, so
    /// it must not be swallowed.
    /// </exception>
    public static DrgFormulaExpression Parse(string formula)
    {
        ArgumentNullException.ThrowIfNull(formula);

        var tokens = DrgFormulaLexer.Tokenize(formula);
        if (tokens.Count == 0)
        {
            throw new DrgFormulaException(formula, 0, "formula is empty");
        }

        var state = new ParseState(formula, tokens);
        var expression = ParseOr(state);

        if (!state.AtEnd)
        {
            throw new DrgFormulaException(
                formula, state.Current.Position,
                $"unexpected \"{state.Current.Text}\" after a complete expression");
        }

        return expression;
    }

    /// <summary>
    /// Parses a formula, returning false rather than throwing. For importers
    /// that need to report every bad row rather than stopping at the first.
    /// </summary>
    public static bool TryParse(string formula, out DrgFormulaExpression? expression)
    {
        try
        {
            expression = Parse(formula);
            return true;
        }
        catch (DrgFormulaException)
        {
            expression = null;
            return false;
        }
    }

    /// <summary>The loosest level: a sequence of AND-terms joined by <c>|</c>.</summary>
    private static DrgFormulaExpression ParseOr(ParseState state)
    {
        var left = ParseAnd(state);

        while (!state.AtEnd && state.Current.Kind == DrgFormulaTokenKind.Or)
        {
            state.Advance();
            left = new DrgFormulaExpression.Or(left, ParseAnd(state));
        }

        return left;
    }

    /// <summary>A sequence of factors joined by <c>&amp;</c>.</summary>
    private static DrgFormulaExpression ParseAnd(ParseState state)
    {
        var left = ParseFactor(state);

        while (!state.AtEnd && state.Current.Kind == DrgFormulaTokenKind.And)
        {
            state.Advance();
            left = new DrgFormulaExpression.And(left, ParseFactor(state));
        }

        return left;
    }

    /// <summary>An operand, a parenthesised expression, or a negation of either.</summary>
    private static DrgFormulaExpression ParseFactor(ParseState state)
    {
        if (state.AtEnd)
        {
            throw new DrgFormulaException(
                state.Formula, state.EndPosition, "formula ends where an operand was expected");
        }

        var token = state.Current;

        switch (token.Kind)
        {
            case DrgFormulaTokenKind.Not:
                state.Advance();
                return new DrgFormulaExpression.Not(ParseFactor(state));

            case DrgFormulaTokenKind.LeftParenthesis:
                state.Advance();
                var inner = ParseOr(state);

                if (state.AtEnd || state.Current.Kind != DrgFormulaTokenKind.RightParenthesis)
                {
                    throw new DrgFormulaException(
                        state.Formula,
                        state.AtEnd ? state.EndPosition : state.Current.Position,
                        "unclosed parenthesis");
                }

                state.Advance();
                return inner;

            case DrgFormulaTokenKind.Operand:
                state.Advance();
                return new DrgFormulaExpression.Operand(token.Text);

            default:
                throw new DrgFormulaException(
                    state.Formula, token.Position,
                    $"expected an operand but found \"{token.Text}\"");
        }
    }

    /// <summary>Position in the token list.</summary>
    private sealed class ParseState(string formula, IReadOnlyList<DrgFormulaToken> tokens)
    {
        private int index;

        internal string Formula { get; } = formula;

        internal bool AtEnd => index >= tokens.Count;

        internal DrgFormulaToken Current => tokens[index];

        /// <summary>One past the last character, for errors about a truncated formula.</summary>
        internal int EndPosition => Formula.Length;

        internal void Advance() => index++;
    }
}
