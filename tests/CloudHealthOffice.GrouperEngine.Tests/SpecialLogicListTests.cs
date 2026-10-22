using CloudHealthOffice.GrouperEngine.Formulas;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The operands whose meaning is not set membership.
///
/// <para>
/// Every expectation here was taken from CMS's own grouper rather than from a
/// reading of it: <c>SpecialNodeCheck</c> in the parity-oracle tool drives
/// CMS's real node supplier over these formulas and masks and prints its
/// answers. That mattered, because asking found two defects that reading had
/// missed — three MCE edit operands spelled in the wrong case, and the belief
/// that a position prefix on <c>incident</c> means anything.
/// </para>
/// </summary>
public class SpecialLogicListTests
{
    private static AttributeMask MaskOf(params string[] entries) =>
        new(entries.Select(OperandName.Parse));

    private static bool Evaluate(string formula, AttributeMask mask, int mdc = 0) =>
        DrgFormulaParser.Parse(formula)
            .Evaluate(new DrgFormulaEvaluation(mask, mdc, ApplySuppression: true));

    /// <summary>
    /// The MCE edits. CMS's tables spell these in lower case and its formulas
    /// spell them in upper case; the resolution is case-insensitive and then
    /// normalises to the tables' spelling, so an upper-case mask entry does
    /// not match. Each row below is CMS's own answer.
    /// </summary>
    [Theory]
    [InlineData("INVALID_PDX", "PDX:invalid_pdx", true)]
    [InlineData("INVALID_PDX", "PDX:INVALID_PDX", false)]
    [InlineData("INVALID_PDX", "invalid_pdx", false)]
    [InlineData("INVALID_SEX", "invalid_sex", true)]
    [InlineData("INVALID_SEX", "INVALID_SEX", false)]
    [InlineData("INVALID_DSTAT", "invalid_dstat", true)]
    [InlineData("INVALID_DSTAT", "INVALID_DSTAT", false)]
    [InlineData("PDX_ECODE", "PDX:pdx_ecode", true)]
    [InlineData("PDX_ECODE", "PDX:PDX_ECODE", false)]
    public void An_mce_edit_looks_for_the_tables_spelling(
        string formula, string maskEntry, bool expected)
    {
        Assert.Equal(expected, Evaluate(formula, MaskOf(maskEntry)));
    }

    /// <summary>
    /// A matching MCE edit also raises the grouper return code, which is how a
    /// claim becomes ungroupable. The boolean does not carry that — a formula
    /// can match for an edit reason or a clinical one and look the same — so
    /// the code has to be collected separately.
    /// </summary>
    [Theory]
    [InlineData("INVALID_DSTAT", "invalid_dstat", 5)]
    [InlineData("INVALID_PDX", "PDX:invalid_pdx", 7)]
    [InlineData("PDX_ECODE", "PDX:pdx_ecode", 1)]
    [InlineData("INVALID_SEX", "invalid_sex", 4)]
    public void A_matching_mce_edit_raises_its_return_code(
        string formula, string maskEntry, int expected)
    {
        var codes = new GrouperReturnCodeSink();

        Assert.True(DrgFormulaParser.Parse(formula).Evaluate(
            new DrgFormulaEvaluation(MaskOf(maskEntry), 0, true, codes)));

        Assert.Equal(expected, codes.Last);
    }

    [Fact]
    public void An_mce_edit_that_does_not_match_raises_nothing()
    {
        var codes = new GrouperReturnCodeSink();

        Assert.False(DrgFormulaParser.Parse("INVALID_PDX").Evaluate(
            new DrgFormulaEvaluation(MaskOf("PDX:strauma"), 0, true, codes)));

        Assert.Null(codes.Last);
        Assert.Empty(codes.Raised);
    }

    /// <summary>
    /// The finding that reading the source would not have given, and the one
    /// most likely to produce a wrong DRG: a position prefix on a special list
    /// name is discarded. <c>PDX:incident</c> and <c>SDX:incident</c> are both
    /// evaluated as <c>ONLY:incident</c>.
    ///
    /// <para>
    /// So the real V44 MDC 15 formula
    /// <c>(PDX:incident &amp; SDX:mdc15fallthrough) | (PDX:mdc15fallthrough
    /// &amp; SDX:incident) | ANY:drg794codes</c> does not test the principal
    /// and secondary positions separately, however much it looks as though it
    /// does. Each row is CMS's answer.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("PDX:incident", "ONLY:incident", true)]
    [InlineData("PDX:incident", "PDX:incident", false)]
    [InlineData("SDX:incident", "ONLY:incident", true)]
    [InlineData("ONLY:incident", "ONLY:incident", true)]
    [InlineData("ONLY:incident", "PDX:incident", false)]
    public void A_prefix_on_a_special_list_name_is_discarded(
        string formula, string maskEntry, bool expected)
    {
        Assert.Equal(expected, Evaluate(formula, MaskOf(maskEntry), mdc: 15));
    }

    /// <summary>
    /// For contrast, an ordinary list. Here the prefix does decide the answer
    /// and the comparison is case-sensitive, which is what makes the special
    /// lists worth singling out.
    /// </summary>
    [Theory]
    [InlineData("PDX:strauma", "PDX:strauma", true)]
    [InlineData("PDX:strauma", "SDX:strauma", false)]
    [InlineData("ORPROC", "ORPROC", true)]
    [InlineData("ORPROC", "orproc", false)]
    [InlineData("ANYDX", "ANYDX", true)]
    public void An_ordinary_operand_honours_its_prefix_and_its_case(
        string formula, string maskEntry, bool expected)
    {
        Assert.Equal(expected, Evaluate(formula, MaskOf(maskEntry), mdc: 8));
    }

    /// <summary>
    /// The code-driven operands cannot be answered from the mask, and CMS
    /// answers false for them when the claim's codes do not meet the
    /// condition — including when the operand's own name is in the mask, which
    /// is what these were checked against.
    ///
    /// <para>
    /// This engine does not implement them yet, and refuses rather than
    /// answering false. Each is the whole condition of the formula it appears
    /// in, so a false answer would move the stay quietly to a different DRG;
    /// there is no safe default and the gap has to be loud.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("MULTST")]
    [InlineData("BILATERAL")]
    [InlineData("FOUR_VESSELS")]
    [InlineData("FOUR_STENTS")]
    [InlineData("MULTFUSE")]
    [InlineData("SINGLEFUSIONCTR")]
    [InlineData("MDC_20_FALL_THRU")]
    public void A_code_driven_operand_refuses_rather_than_answering_false(string operand)
    {
        var error = Assert.Throws<DrgFormulaUnsupportedException>(
            () => Evaluate(operand, MaskOf(operand)));

        Assert.Equal(operand, error.Operand);
    }

    /// <summary>
    /// A real V44 formula that mixes a code-driven operand with an ordinary
    /// one. Short-circuiting does not rescue it: CMS evaluates left first, so
    /// the ordinary operand is tested and then the unsupported one is reached.
    /// </summary>
    [Fact]
    public void A_formula_containing_a_code_driven_operand_cannot_be_evaluated()
    {
        Assert.Throws<DrgFormulaUnsupportedException>(
            () => Evaluate("PDX:strauma & MULTST", MaskOf("PDX:strauma"), mdc: 24));
    }

    /// <summary>
    /// But short-circuiting does rescue it when the left side settles the
    /// answer, exactly as in CMS — the unsupported operand is never reached,
    /// so there is nothing to refuse.
    /// </summary>
    [Fact]
    public void An_unreached_code_driven_operand_is_not_a_problem()
    {
        Assert.False(Evaluate("PDX:strauma & MULTST", MaskOf("SDX:strauma"), mdc: 24));
        Assert.True(Evaluate("PDX:strauma | MULTST", MaskOf("PDX:strauma"), mdc: 24));
    }

    /// <summary>
    /// The special names are resolved without regard to case, as CMS's are, so
    /// a formula that wrote one in a different case still gets the special
    /// node rather than a plain lookup.
    /// </summary>
    [Theory]
    [InlineData("invalid_pdx")]
    [InlineData("Invalid_Pdx")]
    [InlineData("INVALID_PDX")]
    public void A_special_name_is_recognised_in_any_case(string formula)
    {
        Assert.True(Evaluate(formula, MaskOf("PDX:invalid_pdx")));
    }

    /// <summary>
    /// An MCE edit ignores cluster suppression and <c>ONLY:incident</c>
    /// respects it. That asymmetry is CMS's: its edit node does a plain mask
    /// lookup while the incident node is built on the suppression-aware one.
    /// </summary>
    [Fact]
    public void An_mce_edit_ignores_cluster_suppression()
    {
        var suppressedInEight = new AttributeMask([
            new MaskAttribute(
                OperandName.Parse("PDX:invalid_pdx"), MaskAttribute.MdcMask([8])),
            new MaskAttribute(
                OperandName.Parse("ONLY:incident"), MaskAttribute.MdcMask([8])),
        ]);

        Assert.True(DrgFormulaParser.Parse("INVALID_PDX").Evaluate(
            new DrgFormulaEvaluation(suppressedInEight, 8, ApplySuppression: true)));

        Assert.False(DrgFormulaParser.Parse("ONLY:incident").Evaluate(
            new DrgFormulaEvaluation(suppressedInEight, 8, ApplySuppression: true)));
    }

    [Fact]
    public void The_special_lists_are_the_ones_cms_declares()
    {
        Assert.Equal(17, SpecialLogicList.Known.Count);

        Assert.Equal(
            5,
            SpecialLogicList.Known.Count(
                list => list.Kind == SpecialLogicKind.GrouperReturnCode));

        Assert.Single(
            SpecialLogicList.Known,
            list => list.Kind == SpecialLogicKind.OnlyIncident);
    }
}
