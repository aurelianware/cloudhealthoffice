using CloudHealthOffice.GrouperEngine.Formulas;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Operand position prefixes. All the prefixed operands here are real V44
/// tokens, read out of <c>formulaRoot</c> by the table extractor.
/// </summary>
public class OperandNameTests
{
    [Theory]
    [InlineData("ANY:renalfail", OperandPosition.AnyDiagnosis, "renalfail")]
    [InlineData("ANY:diabetes", OperandPosition.AnyDiagnosis, "diabetes")]
    [InlineData("PDX:incident", OperandPosition.PrincipalDiagnosis, "incident")]
    [InlineData("SDX:incident", OperandPosition.SecondaryDiagnosis, "incident")]
    [InlineData("ONLY:incident", OperandPosition.Only, "incident")]
    public void A_prefixed_operand_splits_into_position_and_list(
        string operand, OperandPosition position, string listName)
    {
        var parsed = OperandName.Parse(operand);

        Assert.Equal(position, parsed.Position);
        Assert.Equal(listName, parsed.ListName);
    }

    [Theory]
    [InlineData("CC")]
    [InlineData("backneck")]
    [InlineData("fuse9plus")]
    [InlineData("NORcardcath")]
    [InlineData("INVALID_SEX")]
    public void An_unprefixed_operand_keeps_its_whole_name(string operand)
    {
        var parsed = OperandName.Parse(operand);

        Assert.Equal(OperandPosition.Unqualified, parsed.Position);
        Assert.Equal(operand, parsed.ListName);
    }

    [Fact]
    public void The_same_list_under_three_prefixes_is_three_different_tests()
    {
        // V44 uses all three of these. A grouper that ignored the prefix would
        // collapse them into one test and group the same stay three ways.
        var only = OperandName.Parse("ONLY:incident");
        var principal = OperandName.Parse("PDX:incident");
        var secondary = OperandName.Parse("SDX:incident");

        Assert.Equal("incident", only.ListName);
        Assert.Equal(only.ListName, principal.ListName);
        Assert.Equal(only.ListName, secondary.ListName);

        Assert.NotEqual(only, principal);
        Assert.NotEqual(principal, secondary);
        Assert.NotEqual(only, secondary);
    }

    [Fact]
    public void A_prefix_is_matched_without_regard_to_case()
    {
        // CMS's getPrefixFromString uses equalsIgnoreCase.
        Assert.Equal(OperandPosition.PrincipalDiagnosis, OperandName.Parse("pdx:incident").Position);
        Assert.Equal(OperandPosition.AnyDiagnosis, OperandName.Parse("Any:renalfail").Position);
    }

    [Fact]
    public void An_unrecognised_prefix_is_kept_rather_than_stripped()
    {
        // CMS falls back to no prefix when the string matches no known one, so
        // the colon is part of the list name. Stripping it would silently point
        // the operand at a different list.
        var parsed = OperandName.Parse("WAT:something");

        Assert.Equal(OperandPosition.Unqualified, parsed.Position);
        Assert.Equal("WAT:something", parsed.ListName);
    }

    [Fact]
    public void A_leading_colon_is_not_a_prefix()
    {
        var parsed = OperandName.Parse(":odd");

        Assert.Equal(OperandPosition.Unqualified, parsed.Position);
        Assert.Equal(":odd", parsed.ListName);
    }

    [Theory]
    [InlineData("ANY:renalfail")]
    [InlineData("PDX:incident")]
    [InlineData("SDX:incident")]
    [InlineData("ONLY:incident")]
    [InlineData("PT:age")]
    [InlineData("CC")]
    public void An_operand_round_trips_through_its_rendered_form(string operand)
    {
        Assert.Equal(operand, OperandName.Parse(operand).ToString());
    }

    [Fact]
    public void A_prefixed_operand_lexes_as_a_single_token()
    {
        // CMS does not treat ':' as punctuation, so the prefix must not split
        // the operand during lexing. If it did, "ANY" and "renalfail" would
        // become two adjacent operands and the formula would fail to parse.
        var tokens = DrgFormulaLexer.Tokenize("ANY:renalfail & ANY:diabetes");

        Assert.Equal(3, tokens.Count);
        Assert.Equal("ANY:renalfail", tokens[0].Text);
        Assert.Equal(DrgFormulaTokenKind.And, tokens[1].Kind);
        Assert.Equal("ANY:diabetes", tokens[2].Text);
    }

    [Fact]
    public void A_real_v44_formula_with_prefixes_parses_and_evaluates()
    {
        // V44, MDC 0, DRG 19, rank 22 — the pancreas/kidney transplant rule.
        const string formula =
            "(pancxp | NORislets) & kidneyxp & hemodialysis & ANY:renalfail & ANY:diabetes";

        var expression = DrgFormulaParser.Parse(formula);

        var satisfied = new HashSet<string>(
            ["pancxp", "kidneyxp", "hemodialysis", "ANY:renalfail", "ANY:diabetes"],
            StringComparer.Ordinal);
        Assert.True(expression.Evaluate(satisfied));

        // Drop the dialysis and it is no longer this DRG.
        satisfied.Remove("hemodialysis");
        Assert.False(expression.Evaluate(satisfied));
    }
}
