using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Grouping;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Walking an MDC's ranked formulas. The rows here are invented, with real
/// V44 shapes; what the real table does with them is covered by the gated
/// import test and by the parity oracle.
/// </summary>
public class MsDrgMdcEvaluatorTests
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static MsDrgFormulaRow Row(
        int rank,
        int drg,
        string formula,
        params string[] severitySuppressionOperands) =>
        new(
            Mdc: 5,
            Rank: rank,
            Drg: drg,
            BaseDrg: drg,
            Partition: DrgTablePartition.Medical,
            ReRouteMdc: 0,
            FormulaText: formula,
            Formula: DrgFormulaParser.Parse(formula),
            SeveritySuppressionOperands: severitySuppressionOperands,
            DrgSeverity: 0);

    private static InMemoryFormulaSource Table(params MsDrgFormulaRow[] rows) =>
        new("V440", rows);

    private static MsDrgStay Stay(
        IEnumerable<string>? mask = null,
        ClaimCodes? codes = null,
        bool patientDied = false,
        IReadOnlySet<string>? excluded = null) =>
        new(
            new AttributeMask((mask ?? []).Select(OperandName.Parse)),
            codes ?? ClaimCodes.Empty,
            excluded ?? Nothing,
            Nothing,
            patientDied);

    private static ClaimCodes Claim(params ClaimDiagnosis[] secondaries) =>
        new(
            [ClaimDiagnosis.Create("PRIN1", 5, ComplicationLevel.None, []), .. secondaries],
            []);

    private static ClaimDiagnosis Sdx(
        string code, ComplicationLevel severity, params string[] operands) =>
        ClaimDiagnosis.Create(code, 5, severity, operands);

    /// <summary>
    /// The first match in rank order wins, and nothing below it is tried.
    /// There is no scoring: a later row that also matches is simply never
    /// reached.
    /// </summary>
    [Fact]
    public void The_first_matching_row_in_rank_order_wins()
    {
        var table = Table(
            Row(3, 199, "pneumonia"),
            Row(1, 177, "pneumonia & MCC"),
            Row(2, 178, "pneumonia & CC"));

        var result = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            table,
            Stay(
                mask: ["pneumonia"],
                codes: Claim(Sdx("S1", ComplicationLevel.Cc, "renalfail"))));

        Assert.Equal(178, result.Matched?.Drg);

        // Rank 1 was tried and failed, rank 2 matched, rank 3 was never
        // reached.
        Assert.Equal(2, result.RowsTried);
    }

    /// <summary>
    /// Severity enters the mask per row, so a row that tests <c>MCC</c>
    /// matches on the same claim that a row testing <c>CC</c> does not.
    /// </summary>
    [Fact]
    public void The_severity_operand_is_added_to_the_mask()
    {
        var table = Table(Row(1, 177, "pneumonia & MCC"));

        Assert.Equal(
            177,
            MsDrgMdcEvaluator.EvaluateMdc(
                5,
                table,
                Stay(
                    mask: ["pneumonia"],
                    codes: Claim(Sdx("S1", ComplicationLevel.Mcc, "sepsis"))))
                .Matched?.Drg);

        Assert.Null(
            MsDrgMdcEvaluator.EvaluateMdc(
                5,
                table,
                Stay(
                    mask: ["pneumonia"],
                    codes: Claim(Sdx("S1", ComplicationLevel.Cc, "renalfail"))))
                .Matched);
    }

    /// <summary>
    /// The row's own suppression list is applied before its formula is
    /// evaluated, so the claim's severity differs from row to row. This is the
    /// case a grouper that computed severity once before the loop would get
    /// wrong: the same claim matches the MCC row only where that row does not
    /// suppress the list the MCC came from.
    /// </summary>
    [Fact]
    public void Each_rows_suppression_list_changes_the_severity_it_sees()
    {
        var claim = Claim(Sdx("S1", ComplicationLevel.Mcc, "diabetes"));

        // Rank 1 suppresses the only MCC, so it cannot match its own MCC test
        // and the stay falls through to rank 2.
        var table = Table(
            Row(1, 177, "pneumonia & MCC", "diabetes"),
            Row(2, 179, "pneumonia"));

        var suppressed = MsDrgMdcEvaluator.EvaluateMdc(
            5, table, Stay(mask: ["pneumonia"], codes: claim));

        Assert.Equal(179, suppressed.Matched?.Drg);

        // And the severity reported is the MATCHED row's, not the suppressed
        // one's: rank 2 suppresses nothing, so by the time the stay lands
        // there its only MCC is counting again.
        Assert.Equal(ComplicationLevel.Mcc, suppressed.Severity);

        // The same claim against a rank 1 that suppresses something else.
        var unsuppressed = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            Table(Row(1, 177, "pneumonia & MCC", "renalfail"), Row(2, 179, "pneumonia")),
            Stay(mask: ["pneumonia"], codes: claim));

        Assert.Equal(177, unsuppressed.Matched?.Drg);
        Assert.Equal(ComplicationLevel.Mcc, unsuppressed.Severity);
    }

    /// <summary>
    /// One row's severity does not leak into the next. CMS builds a fresh mask
    /// per row and so does this; a mask mutated in place would leave rank 1's
    /// <c>MCC</c> in the mask for rank 2.
    /// </summary>
    [Fact]
    public void A_rows_severity_does_not_leak_into_the_next_row()
    {
        var table = Table(
            // Rank 1 sees MCC and fails on the procedure it also wants.
            Row(1, 177, "MCC & craniotomy"),
            // Rank 2 suppresses the MCC, so by itself it must not match an
            // MCC test -- and it would, if rank 1's operand were still there.
            Row(2, 178, "MCC", "diabetes"));

        var result = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            table,
            Stay(codes: Claim(Sdx("S1", ComplicationLevel.Mcc, "diabetes"))));

        Assert.Null(result.Matched);
        Assert.Equal(2, result.RowsTried);
    }

    /// <summary>
    /// Rows sharing a rank keep the table's own order, because CMS's sort is
    /// stable. In 14 V44 MDCs an <c>MDCnn</c> row shares a rank with a real
    /// DRG row.
    /// </summary>
    [Fact]
    public void Rows_sharing_a_rank_keep_the_tables_order()
    {
        var first = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            Table(Row(1, 177, "pneumonia"), Row(1, 178, "pneumonia")),
            Stay(mask: ["pneumonia"]));

        Assert.Equal(177, first.Matched?.Drg);

        var reversed = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            Table(Row(1, 178, "pneumonia"), Row(1, 177, "pneumonia")),
            Stay(mask: ["pneumonia"]));

        Assert.Equal(178, reversed.Matched?.Drg);
    }

    /// <summary>
    /// The operands that earned the match are recorded. The DRG number alone
    /// does not say which diagnosis or procedure earned it, and that is what
    /// an appeal turns on.
    /// </summary>
    [Fact]
    public void The_operands_that_earned_the_match_are_recorded()
    {
        var result = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            Table(Row(1, 177, "pneumonia & MCC")),
            Stay(
                mask: ["pneumonia"],
                codes: Claim(Sdx("S1", ComplicationLevel.Mcc, "sepsis"))));

        Assert.Equal(["pneumonia", "MCC"], result.MatchedOperands);
    }

    /// <summary>
    /// An MCE edit that matched raises a grouper return code, and the boolean
    /// match does not carry it: a row can match for an edit reason or a
    /// clinical one and look identical.
    /// </summary>
    [Fact]
    public void A_matched_mce_edit_surfaces_its_return_code()
    {
        var result = MsDrgMdcEvaluator.EvaluateMdc(
            5,
            Table(Row(1, 0, "INVALID_PDX")),
            Stay(mask: ["PDX:invalid_pdx"]));

        Assert.NotNull(result.Matched);
        Assert.False(result.Matched!.AssignsDrg);
        Assert.Equal(7, result.ReturnCode);
    }

    /// <summary>
    /// An MDC the table holds no ranking for matches nothing, and says it
    /// tried nothing rather than reporting a DRG.
    /// </summary>
    [Fact]
    public void An_mdc_the_table_does_not_hold_matches_nothing()
    {
        var result = MsDrgMdcEvaluator.EvaluateMdc(
            23, Table(Row(1, 177, "pneumonia")), Stay(mask: ["pneumonia"]));

        Assert.Null(result.Matched);
        Assert.Equal(0, result.RowsTried);
    }
}
