using CloudHealthOffice.GrouperEngine.Formulas;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The matching rule a mask lookup uses, which is CMS's and is stranger than it
/// looks.
/// </summary>
public class AttributeMaskTests
{
    [Fact]
    public void A_principal_diagnosis_operand_is_not_satisfied_by_a_secondary()
    {
        var mask = new AttributeMask(
            [new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail")]);

        Assert.False(mask.Contains(
            new OperandName(OperandPosition.PrincipalDiagnosis, "renalfail")));
        Assert.True(mask.Contains(
            new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail")));
    }

    [Fact]
    public void An_any_operand_is_satisfied_by_either_position()
    {
        var any = new OperandName(OperandPosition.AnyDiagnosis, "renalfail");

        Assert.True(new AttributeMask(
            [new OperandName(OperandPosition.PrincipalDiagnosis, "renalfail")]).Contains(any));
        Assert.True(new AttributeMask(
            [new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail")]).Contains(any));
    }

    /// <summary>
    /// The reason a mask cannot be a hash set. ANY matches PDX and ANY matches
    /// SDX, but PDX does not match SDX — so the relation is not transitive, and
    /// a relation that is not transitive cannot be hash-code equality. This
    /// test is the guard on that: it fails the moment someone "optimises" the
    /// mask into a <c>HashSet&lt;OperandName&gt;</c>.
    /// </summary>
    [Fact]
    public void The_matching_rule_is_not_transitive()
    {
        var any = new OperandName(OperandPosition.AnyDiagnosis, "renalfail");
        var pdx = new OperandName(OperandPosition.PrincipalDiagnosis, "renalfail");
        var sdx = new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail");

        Assert.True(AttributeMask.Matches(any, pdx));
        Assert.True(AttributeMask.Matches(any, sdx));
        Assert.False(AttributeMask.Matches(pdx, sdx));
    }

    /// <summary>
    /// A procedure operand is unprefixed, and an unprefixed operand does not
    /// reach diagnoses. This is what makes a bare operand in a formula a
    /// procedure or claim-level test rather than a diagnosis one — and why 196
    /// of V44's 452 operands carry a prefix at all.
    /// </summary>
    [Fact]
    public void An_unprefixed_operand_does_not_reach_a_diagnosis()
    {
        var mask = new AttributeMask([
            new OperandName(OperandPosition.PrincipalDiagnosis, "d477"),
            new OperandName(OperandPosition.SecondaryDiagnosis, "d477"),
            new OperandName(OperandPosition.AnyDiagnosis, "d477"),
        ]);

        Assert.False(mask.Contains(new OperandName(OperandPosition.Unqualified, "d477")));
    }

    [Fact]
    public void An_only_operand_matches_nothing_else()
    {
        var only = new OperandName(OperandPosition.Only, "incident");

        Assert.False(new AttributeMask(
            [new OperandName(OperandPosition.PrincipalDiagnosis, "incident")]).Contains(only));
        Assert.False(new AttributeMask(
            [new OperandName(OperandPosition.AnyDiagnosis, "incident")]).Contains(only));
        Assert.True(new AttributeMask([only]).Contains(only));
    }

    [Fact]
    public void Duplicate_contributions_are_kept()
    {
        var mask = new AttributeMask();
        mask.Add(new OperandName(OperandPosition.SecondaryDiagnosis, "stcc"));
        mask.Add(new OperandName(OperandPosition.SecondaryDiagnosis, "stcc"));

        Assert.Equal(2, mask.Count);
    }

    /// <summary>
    /// List names are compared case-sensitively, as CMS compares them. The
    /// prefix is not: CMS resolves a prefix string case-insensitively.
    /// </summary>
    [Fact]
    public void List_names_are_case_sensitive_and_prefixes_are_not()
    {
        var mask = new AttributeMask(
            [new OperandName(OperandPosition.PrincipalDiagnosis, "renalfail")]);

        Assert.False(mask.Contains(OperandName.Parse("PDX:RENALFAIL")));
        Assert.True(mask.Contains(OperandName.Parse("pdx:renalfail")));
    }

    /// <summary>
    /// A formula evaluated against a real mask, with the same list under two
    /// prefixes deciding the answer.
    ///
    /// <para>
    /// Note the operand chosen. An earlier version of this test used
    /// <c>incident</c>, which looks like the perfect example and is the one
    /// case where it does not hold: <c>incident</c> is a special logic list,
    /// so CMS discards the prefix and tests it at <c>ONLY:</c> whatever the
    /// formula wrote. <c>strauma</c> is an ordinary list and does behave as it
    /// reads. See <see cref="SpecialLogicListTests"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void A_formula_evaluates_against_a_mask()
    {
        var formula = DrgFormulaParser.Parse("PDX:strauma & ~SDX:strauma");

        var principalOnly = new AttributeMask(
            [new OperandName(OperandPosition.PrincipalDiagnosis, "strauma")]);
        Assert.True(formula.Evaluate(principalOnly));

        var both = new AttributeMask([
            new OperandName(OperandPosition.PrincipalDiagnosis, "strauma"),
            new OperandName(OperandPosition.SecondaryDiagnosis, "strauma"),
        ]);
        Assert.False(formula.Evaluate(both));
    }

    [Fact]
    public void Evaluating_against_a_mask_records_what_matched()
    {
        var formula = DrgFormulaParser.Parse("ANY:renalfail & (pancxp | kidneyxp)");
        var mask = new AttributeMask([
            new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail"),
            new OperandName(OperandPosition.Unqualified, "kidneyxp"),
        ]);

        var matched = new List<string>();
        Assert.True(formula.Evaluate(mask, matched));

        // pancxp was tested and failed, so it is not recorded; kidneyxp was
        // reached only because pancxp failed.
        Assert.Equal(["ANY:renalfail", "kidneyxp"], matched);
    }
}
