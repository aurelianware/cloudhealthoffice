using System.Text;

namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// Splits a DRG logic formula into tokens.
///
/// <para>
/// The language has six token kinds and nothing else: an operand name, the two
/// parentheses, and the three logical operators <c>&amp;</c>, <c>|</c> and
/// <c>~</c>. Anything that is not one of those five characters or whitespace is
/// part of an operand name, so there is no identifier rule to get wrong and no
/// reserved words. Whitespace separates operands and is otherwise discarded.
/// </para>
///
/// <para>
/// This mirrors CMS's own lexer, which treats any substring outside its
/// punctuation set as an atom. Keeping that rule exactly means an operand name
/// CMS invents in a future version — digits, mixed case, whatever — needs no
/// change here.
/// </para>
/// </summary>
public static class DrgFormulaLexer
{
    /// <summary>Tokenises <paramref name="formula"/>.</summary>
    /// <exception cref="DrgFormulaException">
    /// Never. The lexer cannot fail: every character is either punctuation,
    /// whitespace, or part of an operand name. Errors are the parser's business.
    /// </exception>
    public static IReadOnlyList<DrgFormulaToken> Tokenize(string formula)
    {
        ArgumentNullException.ThrowIfNull(formula);

        var tokens = new List<DrgFormulaToken>();
        var operand = new StringBuilder();
        var operandStart = 0;

        void FlushOperand()
        {
            if (operand.Length == 0)
            {
                return;
            }

            tokens.Add(new DrgFormulaToken(
                DrgFormulaTokenKind.Operand, operand.ToString(), operandStart));
            operand.Clear();
        }

        for (var i = 0; i < formula.Length; i++)
        {
            var c = formula[i];

            var punctuation = c switch
            {
                '(' => DrgFormulaTokenKind.LeftParenthesis,
                ')' => DrgFormulaTokenKind.RightParenthesis,
                '&' => DrgFormulaTokenKind.And,
                '|' => DrgFormulaTokenKind.Or,
                '~' => DrgFormulaTokenKind.Not,
                _ => (DrgFormulaTokenKind?)null,
            };

            if (punctuation is not null)
            {
                FlushOperand();
                tokens.Add(new DrgFormulaToken(punctuation.Value, c.ToString(), i));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushOperand();
                continue;
            }

            if (operand.Length == 0)
            {
                operandStart = i;
            }

            operand.Append(c);
        }

        FlushOperand();
        return tokens;
    }
}
