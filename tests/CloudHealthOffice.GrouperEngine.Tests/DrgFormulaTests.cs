using CloudHealthOffice.GrouperEngine.Formulas;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The DRG logic formula engine: lexing, parsing, precedence and evaluation.
///
/// <para>
/// The formula strings here are not invented. Each one marked "real V44" was
/// read out of CMS's own table buffer by
/// <c>tools/cms-grouper-table-extractor</c>, so the precedence rules these
/// tests pin are the ones CMS's data actually relies on.
/// </para>
/// </summary>
public class DrgFormulaTests
{
    private static IReadOnlySet<string> Satisfied(params string[] operands) =>
        new HashSet<string>(operands, StringComparer.Ordinal);

    // ─────────────────────────────────────────────────────────
    //  Lexing
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void Whitespace_separates_operands_and_is_otherwise_discarded()
    {
        var tokens = DrgFormulaLexer.Tokenize("  fuse9plus |   cmadifd  ");

        Assert.Equal(3, tokens.Count);
        Assert.Equal(DrgFormulaTokenKind.Operand, tokens[0].Kind);
        Assert.Equal("fuse9plus", tokens[0].Text);
        Assert.Equal(DrgFormulaTokenKind.Or, tokens[1].Kind);
        Assert.Equal("cmadifd", tokens[2].Text);
    }

    [Fact]
    public void Punctuation_needs_no_surrounding_space()
    {
        var tokens = DrgFormulaLexer.Tokenize("(a|b)&~c");

        Assert.Equal(
            new[]
            {
                DrgFormulaTokenKind.LeftParenthesis,
                DrgFormulaTokenKind.Operand,
                DrgFormulaTokenKind.Or,
                DrgFormulaTokenKind.Operand,
                DrgFormulaTokenKind.RightParenthesis,
                DrgFormulaTokenKind.And,
                DrgFormulaTokenKind.Not,
                DrgFormulaTokenKind.Operand,
            },
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void An_operand_name_may_hold_digits_and_mixed_case()
    {
        // Real V44 operand names. CMS treats any non-punctuation substring as an
        // atom, so there is no identifier rule to violate.
        var tokens = DrgFormulaLexer.Tokenize("NORcardcath | fuse9plus");

        Assert.Equal("NORcardcath", tokens[0].Text);
        Assert.Equal("fuse9plus", tokens[2].Text);
    }

    [Fact]
    public void A_token_records_where_it_started()
    {
        var tokens = DrgFormulaLexer.Tokenize("CC & backneck");

        Assert.Equal(0, tokens[0].Position);
        Assert.Equal(3, tokens[1].Position);
        Assert.Equal(5, tokens[2].Position);
    }

    // ─────────────────────────────────────────────────────────
    //  Precedence
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void And_binds_tighter_than_or()
    {
        var expression = DrgFormulaParser.Parse("a | b & c");

        // a | (b & c): satisfying b alone must not be enough.
        Assert.False(expression.Evaluate(Satisfied("b")));
        Assert.True(expression.Evaluate(Satisfied("b", "c")));
        Assert.True(expression.Evaluate(Satisfied("a")));
    }

    [Fact]
    public void Not_binds_tighter_than_and()
    {
        var expression = DrgFormulaParser.Parse("~a & b");

        // (~a) & b, not ~(a & b).
        Assert.True(expression.Evaluate(Satisfied("b")));
        Assert.False(expression.Evaluate(Satisfied("a", "b")));
        Assert.False(expression.Evaluate(Satisfied()));
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        var loose = DrgFormulaParser.Parse("a | b & c");
        var tight = DrgFormulaParser.Parse("(a | b) & c");

        var onlyA = Satisfied("a");
        Assert.True(loose.Evaluate(onlyA));
        Assert.False(tight.Evaluate(onlyA));
    }

    [Fact]
    public void Or_is_left_associative_and_chains_without_parentheses()
    {
        // Real V44, MDC 8: DRG 525.
        var expression = DrgFormulaParser.Parse("fuse9plus | cmadifd | tulip");

        Assert.True(expression.Evaluate(Satisfied("tulip")));
        Assert.True(expression.Evaluate(Satisfied("fuse9plus")));
        Assert.False(expression.Evaluate(Satisfied("something-else")));
    }

    // ─────────────────────────────────────────────────────────
    //  Evaluation against real V44 formulas
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_real_severity_split_formula_needs_both_operands()
    {
        // Real V44, MDC 8: DRG 519, rank 74.
        var expression = DrgFormulaParser.Parse("(CC & backneck)");

        Assert.True(expression.Evaluate(Satisfied("CC", "backneck")));
        Assert.False(expression.Evaluate(Satisfied("CC")));
        Assert.False(expression.Evaluate(Satisfied("backneck")));
    }

    [Fact]
    public void A_real_procedure_formula_requires_the_bypass_as_well_as_either_approach()
    {
        // Real V44, MDC 5: DRG 234, rank 16.
        var expression = DrgFormulaParser.Parse("(sensor | NORcardcath) & bypass");

        Assert.True(expression.Evaluate(Satisfied("sensor", "bypass")));
        Assert.True(expression.Evaluate(Satisfied("NORcardcath", "bypass")));

        // Either approach without the bypass is not this DRG. A parser that
        // mis-binds the parentheses would pass both of these.
        Assert.False(expression.Evaluate(Satisfied("sensor")));
        Assert.False(expression.Evaluate(Satisfied("NORcardcath")));
        Assert.False(expression.Evaluate(Satisfied("bypass")));
    }

    [Fact]
    public void Operand_names_are_matched_case_sensitively()
    {
        // CMS's own names differ only in case in places, so folding case here
        // would silently merge distinct operands.
        var expression = DrgFormulaParser.Parse("CC");

        Assert.True(expression.Evaluate(Satisfied("CC")));
        Assert.False(expression.Evaluate(Satisfied("cc")));
    }

    [Fact]
    public void An_unsatisfied_operand_set_is_not_an_error()
    {
        var expression = DrgFormulaParser.Parse("CC & backneck");

        Assert.False(expression.Evaluate(Satisfied()));
    }

    /// <summary>
    /// Every case here was run through CMS's own lexer and parser by
    /// <c>tools/cms-grouper-parity-oracle</c>'s
    /// <c>FormulaPrecedenceCheck</c>, which agreed with all of them. Precedence
    /// is the part of this language most easily got wrong and least likely to
    /// fail loudly: a parser that binds <c>a | b &amp; c</c> the other way
    /// still returns a perfectly good boolean for every formula, just the wrong
    /// one, and only on the claims where it matters.
    /// </summary>
    [Theory]
    [InlineData("a | b & c", "b", false)]
    [InlineData("a | b & c", "b,c", true)]
    [InlineData("a | b & c", "a", true)]
    [InlineData("~a & b", "b", true)]
    [InlineData("~a & b", "a,b", false)]
    [InlineData("~a & b", "", false)]
    [InlineData("(a | b) & c", "a", false)]
    [InlineData("fuse9plus | cmadifd | tulip", "tulip", true)]
    [InlineData("(CC & backneck)", "CC", false)]
    [InlineData("(sensor | NORcardcath) & bypass", "sensor", false)]
    [InlineData("(sensor | NORcardcath) & bypass", "sensor,bypass", true)]
    [InlineData("~a | b & ~c", "b", true)]
    [InlineData("~(a & b)", "a", true)]
    [InlineData("a & b | c & d", "b,c", false)]
    public void Precedence_matches_the_cms_parser(string formula, string operands, bool expected)
    {
        var satisfied = Satisfied(
            operands.Split(',', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expected, DrgFormulaParser.Parse(formula).Evaluate(satisfied));
    }

    // ─────────────────────────────────────────────────────────
    //  Printing
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_parsed_formula_prints_back_to_an_equivalent_formula()
    {
        const string original = "(sensor | NORcardcath) & bypass";
        var printed = DrgFormulaParser.Parse(original).Print();

        // Parentheses are normalised rather than preserved, so compare by
        // behaviour: what matters for an audit record is that the printed form
        // means the same thing.
        var reparsed = DrgFormulaParser.Parse(printed);
        foreach (var operands in new[]
        {
            Satisfied("sensor", "bypass"),
            Satisfied("NORcardcath", "bypass"),
            Satisfied("sensor"),
            Satisfied("bypass"),
            Satisfied(),
        })
        {
            Assert.Equal(
                DrgFormulaParser.Parse(original).Evaluate(operands),
                reparsed.Evaluate(operands));
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Malformed formulas
    // ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(a | b")]
    [InlineData("a &")]
    [InlineData("a | | b")]
    [InlineData("~")]
    [InlineData(")a(")]
    [InlineData("a b)")]
    public void A_malformed_formula_throws_rather_than_evaluating_to_false(string formula)
    {
        // This is always a table-import problem, never a claim problem, so it
        // must surface rather than quietly becoming "this DRG did not match".
        var exception = Assert.Throws<DrgFormulaException>(() => DrgFormulaParser.Parse(formula));
        Assert.Equal(formula, exception.Formula);
    }

    [Fact]
    public void Try_parse_reports_failure_without_throwing()
    {
        Assert.False(DrgFormulaParser.TryParse("(a | b", out var failed));
        Assert.Null(failed);

        Assert.True(DrgFormulaParser.TryParse("a | b", out var parsed));
        Assert.NotNull(parsed);
    }

    // ─────────────────────────────────────────────────────────
    //  Accumulation: which operands earned the answer
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void Evaluation_records_the_operands_that_tested_true()
    {
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("(CC & backneck)");

        Assert.True(expression.Evaluate(Satisfied("CC", "backneck"), matched));
        Assert.Equal(["CC", "backneck"], matched);
    }

    [Fact]
    public void Only_satisfied_operands_are_recorded()
    {
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("CC & backneck");

        Assert.False(expression.Evaluate(Satisfied("CC"), matched));
        Assert.Equal(["CC"], matched);
    }

    [Fact]
    public void A_short_circuited_or_does_not_record_the_right_side()
    {
        // CMS's Or is Java "||": left first, right skipped once the answer is
        // settled. "tulip" is satisfied here but never tested, so it is not
        // part of why this DRG matched.
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("fuse9plus | cmadifd | tulip");

        Assert.True(expression.Evaluate(Satisfied("fuse9plus", "tulip"), matched));
        Assert.Equal(["fuse9plus"], matched);
    }

    [Fact]
    public void A_short_circuited_and_does_not_record_the_right_side()
    {
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("sensor & bypass");

        Assert.False(expression.Evaluate(Satisfied("bypass"), matched));
        Assert.Empty(matched);
    }

    [Fact]
    public void A_negated_operand_that_tested_true_is_still_recorded()
    {
        // In "~a" the lookup on "a" succeeded, and CMS accumulates on success,
        // before the negation flips the result. So the operand is recorded even
        // though the expression is false — it is the reason the expression is
        // false.
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("~CC");

        Assert.False(expression.Evaluate(Satisfied("CC"), matched));
        Assert.Equal(["CC"], matched);
    }

    [Fact]
    public void An_unsatisfied_negated_operand_records_nothing()
    {
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("~CC");

        Assert.True(expression.Evaluate(Satisfied(), matched));
        Assert.Empty(matched);
    }

    [Fact]
    public void Operands_are_recorded_in_evaluation_order()
    {
        var matched = new List<string>();
        var expression = DrgFormulaParser.Parse("(sensor | NORcardcath) & bypass");

        Assert.True(expression.Evaluate(Satisfied("NORcardcath", "bypass"), matched));

        // sensor was tested and failed, so it is absent; NORcardcath then
        // satisfied the left side, and bypass the right.
        Assert.Equal(["NORcardcath", "bypass"], matched);
    }

    [Fact]
    public void A_duplicate_operand_is_recorded_once_per_evaluation()
    {
        // The same operand can appear twice in one formula. Each successful
        // lookup is a separate accumulation, which is what CMS does with a Set
        // of attributes — so a caller wanting distinct operands should collect
        // into a set rather than a list.
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var expression = DrgFormulaParser.Parse("CC & (CC | backneck)");

        Assert.True(expression.Evaluate(Satisfied("CC"), distinct));
        Assert.Equal(["CC"], distinct);
    }

    [Fact]
    public void Evaluation_without_an_accumulator_still_answers()
    {
        var expression = DrgFormulaParser.Parse("(sensor | NORcardcath) & bypass");

        Assert.True(expression.Evaluate(Satisfied("sensor", "bypass")));
        Assert.True(expression.Evaluate(Satisfied("sensor", "bypass"), null));
    }

    [Fact]
    public void Two_adjacent_operands_are_rejected()
    {
        // "a b" has no operator between the operands. Accepting it would mean
        // silently dropping half the formula.
        Assert.Throws<DrgFormulaException>(() => DrgFormulaParser.Parse("a b"));
    }
}
