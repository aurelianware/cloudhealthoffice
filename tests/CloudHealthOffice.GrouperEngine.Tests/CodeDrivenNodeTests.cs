using CloudHealthOffice.GrouperEngine.Formulas;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The eleven V44 operands that read the claim's codes rather than its mask.
///
/// <para>
/// Every answer below is CMS's own, printed by
/// <c>tools/cms-grouper-parity-oracle</c>'s <c>CodeDrivenNodeCheck</c>, which
/// drives CMS's real node supplier over synthetic codes. Reading the source
/// was not enough: four of the rules are not what the operand names suggest,
/// and a fifth trap is in the naming itself.
/// </para>
/// </summary>
public class CodeDrivenNodeTests
{
    private static ClaimDiagnosis Dx(int mdc, params string[] operands) =>
        ClaimDiagnosis.Create("Z000", mdc, operands);

    private static ClaimProcedure Pr(params string[] operands) =>
        ClaimProcedure.Create("0JH00ZZ", operands);

    private static bool Evaluate(string formula, params ClaimDiagnosis[] diagnoses) =>
        Evaluate(formula, new ClaimCodes(diagnoses, []));

    private static bool EvaluatePr(string formula, params ClaimProcedure[] procedures) =>
        Evaluate(formula, new ClaimCodes([], procedures));

    private static bool Evaluate(string formula, ClaimCodes codes) =>
        DrgFormulaParser.Parse(formula).Evaluate(
            new DrgFormulaEvaluation(
                new AttributeMask(), Mdc: 0, ApplySuppression: true, Claim: codes));

    /// <summary>
    /// <c>MULTST</c>: two different significant-trauma lists among the
    /// diagnoses.
    ///
    /// <para>
    /// The rule that a reading would miss is the last row: <b>one code
    /// contributes at most one list</b>. CMS takes from each code the first
    /// list it carries that has not already been found and then moves to the
    /// next code, so a single diagnosis on both the head and chest lists does
    /// not make a multiple-trauma stay. Two codes carrying one each do.
    /// </para>
    /// </summary>
    [Fact]
    public void Significant_trauma_wants_two_lists_from_two_different_codes()
    {
        Assert.False(Evaluate("MULTST", Dx(24, "sthead")));
        Assert.True(Evaluate("MULTST", Dx(24, "sthead"), Dx(24, "stchest")));
        Assert.False(Evaluate("MULTST", Dx(24, "sthead"), Dx(24, "sthead")));
        Assert.True(Evaluate(
            "MULTST", Dx(24, "sthead"), Dx(24, "sthead"), Dx(24, "stchest")));
        Assert.True(Evaluate("MULTST", Dx(24, "stlolimb"), Dx(24, "sthead")));
        Assert.False(Evaluate("MULTST"));

        // One code carrying both lists is not enough.
        Assert.False(Evaluate("MULTST", Dx(24, "sthead", "stchest")));
    }

    /// <summary>
    /// A diagnosis list name is compared case-insensitively and without its
    /// position prefix, because CMS's diagnosis lookup is a walk comparing
    /// list names with <c>equalsIgnoreCase</c>. The procedure side is the
    /// opposite; see
    /// <see cref="Bilateral_compares_a_procedure_list_name_exactly"/>.
    /// </summary>
    [Fact]
    public void A_diagnosis_list_name_is_matched_loosely()
    {
        Assert.True(Evaluate("MULTST", Dx(24, "STHEAD"), Dx(24, "stchest")));
        Assert.True(Evaluate("MULTST", Dx(24, "PDX:sthead"), Dx(24, "SDX:stchest")));
    }

    /// <summary>
    /// The operand name itself resolves case-insensitively, and a position
    /// prefix on it is discarded — as for every special list.
    /// </summary>
    [Theory]
    [InlineData("MULTST")]
    [InlineData("multst")]
    [InlineData("PDX:MULTST")]
    public void The_operand_name_resolves_loosely(string operand)
    {
        Assert.True(Evaluate(operand, Dx(24, "sthead"), Dx(24, "stchest")));
    }

    /// <summary>
    /// <c>BILATERAL</c>: the same shape as significant trauma, over the
    /// procedures.
    ///
    /// <para>
    /// Two rows here are worth stating plainly. It takes <b>any two</b> of its
    /// six lists, so a right hip and a right knee satisfy it — it does not
    /// check that the two are a left/right pair, whatever the name says. And
    /// as with trauma, one code contributes at most one list, so a single
    /// procedure on both the right-hip and left-hip lists does not satisfy it.
    /// </para>
    /// </summary>
    [Fact]
    public void Bilateral_wants_any_two_lists_from_two_different_codes()
    {
        Assert.False(EvaluatePr("BILATERAL", Pr("righthip")));
        Assert.True(EvaluatePr("BILATERAL", Pr("righthip"), Pr("lefthip")));
        Assert.False(EvaluatePr("BILATERAL", Pr("righthip"), Pr("righthip")));

        // Two lists on the same side still count.
        Assert.True(EvaluatePr("BILATERAL", Pr("righthip"), Pr("rightknee")));

        // One code carrying both sides does not.
        Assert.False(EvaluatePr("BILATERAL", Pr("righthip", "lefthip")));
    }

    /// <summary>
    /// A procedure list name is matched as a whole attribute: case-sensitively
    /// and prefix included. CMS's procedure lookup is a hash-set lookup rather
    /// than the diagnosis side's walk, and the difference is observable — a
    /// diagnosis carrying <c>STHEAD</c> satisfies <c>MULTST</c>, a procedure
    /// carrying <c>RIGHTHIP</c> does not satisfy <c>BILATERAL</c>.
    /// </summary>
    [Fact]
    public void Bilateral_compares_a_procedure_list_name_exactly()
    {
        Assert.False(EvaluatePr("BILATERAL", Pr("RIGHTHIP"), Pr("lefthip")));
        Assert.False(EvaluatePr("BILATERAL", Pr("PDX:righthip"), Pr("lefthip")));
    }

    /// <summary>
    /// <c>SINGLEFUSIONCTR</c> wants both its lists <i>and</i> three
    /// occurrences in total. Unlike the bilateral nodes, the counting nodes
    /// count every list a code carries, so one code can contribute twice.
    /// </summary>
    [Fact]
    public void The_single_fusion_counter_wants_both_lists_and_three_occurrences()
    {
        Assert.False(EvaluatePr(
            "SINGLEFUSIONCTR", Pr("sglantfuse"), Pr("sglpostfuse")));
        Assert.True(EvaluatePr(
            "SINGLEFUSIONCTR", Pr("sglantfuse"), Pr("sglantfuse"), Pr("sglpostfuse")));
        Assert.True(EvaluatePr(
            "SINGLEFUSIONCTR", Pr("sglantfuse", "sglpostfuse"), Pr("sglantfuse")));
        Assert.False(EvaluatePr(
            "SINGLEFUSIONCTR",
            Pr("sglantfuse"), Pr("sglantfuse"), Pr("sglantfuse")));
    }

    /// <summary>
    /// <c>MULTFUSE</c> wants two occurrences and does <b>not</b> require both
    /// its lists, where <c>SINGLEFUSIONCTR</c> does. The same list twice
    /// satisfies the one and not the other, and they are built from the same
    /// two fusion lists — so treating the counting operands as one rule would
    /// be wrong in both directions.
    /// </summary>
    [Fact]
    public void The_multiple_fusion_counter_has_no_distinctness_requirement()
    {
        Assert.False(EvaluatePr("MULTFUSE", Pr("sglantfuse")));
        Assert.True(EvaluatePr("MULTFUSE", Pr("sglantfuse"), Pr("sglantfuse")));
        Assert.True(EvaluatePr("MULTFUSE", Pr("sglantfuse", "sglpostfuse1")));

        // sglpostfuse is on SINGLEFUSIONCTR's lists, not on this one's.
        Assert.False(EvaluatePr("MULTFUSE", Pr("sglpostfuse"), Pr("sglpostfuse")));
    }

    /// <summary>
    /// The four single-list counters, each wanting two occurrences of its one
    /// list. <c>sglpostfuse</c> and <c>sglpostfuse1</c> are different lists,
    /// and <c>sglantsectXfuse</c>'s capital X is the tables' spelling.
    /// </summary>
    [Fact]
    public void The_single_list_counters_want_two_occurrences_of_their_own_list()
    {
        Assert.False(EvaluatePr("SGLANTCTR", Pr("sglantfuse")));
        Assert.True(EvaluatePr("SGLANTCTR", Pr("sglantfuse"), Pr("sglantfuse")));

        Assert.True(EvaluatePr(
            "SGLPOST1CTR", Pr("sglpostfuse1"), Pr("sglpostfuse1")));
        Assert.False(EvaluatePr(
            "SGLPOST1CTR", Pr("sglpostfuse"), Pr("sglpostfuse")));

        Assert.True(EvaluatePr(
            "SGLPOSTFUSECTR", Pr("sglpostfuse"), Pr("sglpostfuse")));

        Assert.True(EvaluatePr(
            "SGLANTSECTXCTR", Pr("sglantsectXfuse"), Pr("sglantsectXfuse")));
        Assert.False(EvaluatePr(
            "SGLANTSECTXCTR", Pr("sglantsectxfuse"), Pr("sglantsectxfuse")));
    }

    /// <summary>
    /// Two of CMS's node-factory members are not named after the operand they
    /// match: <c>SGLANTFUSECTR</c> matches <c>SGLANTCTR</c> and
    /// <c>SGLPOST1FUSECTR</c> matches <c>SGLPOST1CTR</c>. So the member names
    /// are ordinary operands, counting nothing — here with the procedures that
    /// would have satisfied the real counter and an empty mask, which is the
    /// case CMS was asked. A table keyed on the member names would have left
    /// both counters dead with nothing to notice.
    /// </summary>
    [Theory]
    [InlineData("SGLANTFUSECTR", "sglantfuse")]
    [InlineData("SGLPOST1FUSECTR", "sglpostfuse1")]
    public void The_enums_java_name_counts_nothing(string operand, string listName)
    {
        Assert.False(EvaluatePr(operand, Pr(listName), Pr(listName)));
    }

    /// <summary>
    /// <c>FOUR_VESSELS</c> and <c>FOUR_STENTS</c> count <b>weighted</b>
    /// occurrences: <c>vessel3</c> is worth three. So one code can satisfy the
    /// operand on its own, and three <c>vessel1</c> codes cannot — which is
    /// the opposite of what "four vessels" and a count of codes would suggest.
    /// </summary>
    [Fact]
    public void The_vessel_and_stent_counts_are_weighted()
    {
        Assert.True(EvaluatePr("FOUR_VESSELS", Pr("vessel4")));
        Assert.False(EvaluatePr("FOUR_VESSELS", Pr("vessel3")));
        Assert.True(EvaluatePr("FOUR_VESSELS", Pr("vessel2"), Pr("vessel2")));
        Assert.True(EvaluatePr(
            "FOUR_VESSELS",
            Pr("vessel1"), Pr("vessel1"), Pr("vessel1"), Pr("vessel1")));
        Assert.False(EvaluatePr(
            "FOUR_VESSELS", Pr("vessel1"), Pr("vessel1"), Pr("vessel1")));
        Assert.True(EvaluatePr("FOUR_VESSELS", Pr("vessel1", "vessel3")));

        Assert.True(EvaluatePr("FOUR_STENTS", Pr("stent4")));
        Assert.False(EvaluatePr(
            "FOUR_STENTS", Pr("stent1"), Pr("stent1"), Pr("stent1")));
        Assert.False(EvaluatePr("FOUR_STENTS", Pr("vessel4")));
    }

    /// <summary>
    /// <c>MDC_20_FALL_THRU</c> reads the principal diagnosis and nothing else,
    /// so the claim's diagnosis order decides it. A secondary in MDC 20 does
    /// not satisfy it.
    /// </summary>
    [Fact]
    public void Mdc_20_fall_through_reads_the_principal_diagnosis_only()
    {
        Assert.True(Evaluate("MDC_20_FALL_THRU", Dx(20), Dx(5)));
        Assert.False(Evaluate("MDC_20_FALL_THRU", Dx(5), Dx(20)));
        Assert.False(Evaluate("MDC_20_FALL_THRU"));
    }

    /// <summary>
    /// Every code-driven list in the table has a rule, so none of them refuses
    /// a claim. This is the assertion that would fail if a new fiscal year
    /// added a special list and nothing taught the engine what it means.
    /// </summary>
    [Fact]
    public void Every_code_driven_list_is_implemented()
    {
        var unimplemented = SpecialLogicList.Known
            .Where(list => list.Kind == SpecialLogicKind.CodeDriven)
            .Where(list => list.Rule == CodeDrivenRule.None)
            .Select(list => list.CanonicalName);

        Assert.Empty(unimplemented);
    }

    /// <summary>
    /// A formula mixing a code-driven operand with an ordinary one, which is
    /// how V44 actually writes them. The mask answers one half and the claim's
    /// codes the other.
    /// </summary>
    [Fact]
    public void A_formula_can_mix_a_code_driven_operand_with_a_mask_lookup()
    {
        var mask = new AttributeMask(
            [OperandName.Parse("PDX:strauma")]);
        var codes = new ClaimCodes([Dx(24, "sthead"), Dx(24, "stchest")], []);
        var formula = DrgFormulaParser.Parse("PDX:strauma & MULTST");

        Assert.True(formula.Evaluate(
            new DrgFormulaEvaluation(mask, Mdc: 24, ApplySuppression: true, Claim: codes)));

        var oneTraumaList = new ClaimCodes([Dx(24, "sthead")], []);
        Assert.False(formula.Evaluate(new DrgFormulaEvaluation(
            mask, Mdc: 24, ApplySuppression: true, Claim: oneTraumaList)));
    }
}
