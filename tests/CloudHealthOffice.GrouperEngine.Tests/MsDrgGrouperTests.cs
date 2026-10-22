using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Grouping;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Choosing the MDC, and what that choice does to the DRG.
///
/// <para>
/// The rows here are invented, with real V44 shapes: the pre-MDC pass holding
/// edits and reroutes, a principal-diagnosis MDC holding the DRG ranking, and
/// MDC 29 as a shared destination. What the real table does is covered by the
/// gated import test; what CMS does is read off its chain, and the end-to-end
/// parity run is what will confirm the order.
/// </para>
/// </summary>
public class MsDrgGrouperTests
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static MsDrgFormulaRow Row(
        int mdc, int rank, int drg, string formula, int reRouteMdc = 0) =>
        new(
            Mdc: mdc,
            Rank: rank,
            Drg: drg,
            BaseDrg: drg,
            Partition: drg == 0 ? DrgTablePartition.NotApplicable : DrgTablePartition.Medical,
            ReRouteMdc: reRouteMdc,
            FormulaText: formula,
            Formula: DrgFormulaParser.Parse(formula),
            SeveritySuppressionOperands: [string.Empty],
            DrgSeverity: 0);

    private static InMemoryFormulaSource Table(params MsDrgFormulaRow[] rows) =>
        new("V440", rows);

    private static MsDrgStay Stay(
        int principalMdc,
        IEnumerable<string>? mask = null,
        ComplicationLevel secondarySeverity = ComplicationLevel.None) =>
        new(
            new AttributeMask((mask ?? []).Select(OperandName.Parse)),
            new ClaimCodes(
                [
                    ClaimDiagnosis.Create("PRIN1", principalMdc, ComplicationLevel.None, []),
                    ClaimDiagnosis.Create("SEC1", principalMdc, secondarySeverity, ["sepsis"]),
                ],
                []),
            Nothing,
            Nothing,
            PatientDied: false);

    /// <summary>
    /// The ordinary path: the pre-MDC pass matches nothing, and the stay
    /// groups under its principal diagnosis's own MDC — read off the code
    /// table, not searched for.
    /// </summary>
    [Fact]
    public void A_stay_groups_under_its_principal_diagnosiss_mdc()
    {
        var table = Table(
            Row(0, 1, 0, "MDC00"),
            Row(5, 1, 280, "ANY:ami"));

        var result = MsDrgGrouper.Group(table, Stay(5, mask: ["PDX:ami"]));

        Assert.Equal(280, result.Drg);
        Assert.Equal(5, result.Mdc);
        Assert.Equal([0, 5], result.MdcStops.Stops);
    }

    /// <summary>
    /// A pre-MDC match skips the principal diagnosis's MDC altogether. This is
    /// how the transplant DRGs work: they are reached before any body system
    /// is considered, and the stay's own MDC is never walked.
    /// </summary>
    [Fact]
    public void A_pre_mdc_match_skips_the_principal_diagnosiss_mdc()
    {
        var table = Table(
            Row(0, 1, 5, "livertx"),
            Row(7, 1, 420, "PDX:liver"));

        var result = MsDrgGrouper.Group(table, Stay(7, mask: ["livertx", "PDX:liver"]));

        Assert.Equal(5, result.Drg);
        Assert.Equal([0], result.MdcStops.Stops);

        // The only stop was the pre-MDC pass, so the reported MDC falls back
        // to the principal diagnosis's.
        Assert.Equal(7, result.Mdc);
    }

    /// <summary>
    /// An MCE edit in the pre-MDC pass refuses the claim. The edit is not a
    /// DRG: it matches, and the return code it raises is what makes the stay
    /// ungroupable.
    /// </summary>
    [Fact]
    public void An_edit_in_the_pre_mdc_pass_refuses_the_claim()
    {
        var table = Table(
            Row(0, 1, 0, "invalid_pdx"),
            Row(5, 1, 280, "ANY:ami"));

        var result = MsDrgGrouper.Group(
            table, Stay(5, mask: ["PDX:invalid_pdx", "PDX:ami"]));

        Assert.False(result.Groupable);
        Assert.Equal(999, result.Drg);
        Assert.Equal(7, result.ReturnCode);
        Assert.Equal(0, result.Mdc);

        // The principal diagnosis's MDC was never walked.
        Assert.Equal([0], result.MdcStops.Stops);
    }

    /// <summary>
    /// A pre-MDC row can route the stay out to another MDC, which is how
    /// multiple trauma and HIV are grouped, and the destination is the MDC
    /// reported.
    /// </summary>
    [Fact]
    public void A_pre_mdc_row_reroutes_the_stay()
    {
        var table = Table(
            Row(0, 1, 0, "PDX:hiv", reRouteMdc: 25),
            Row(25, 1, 974, "PDX:hiv"),
            Row(5, 1, 280, "ANY:ami"));

        var result = MsDrgGrouper.Group(table, Stay(5, mask: ["PDX:hiv", "PDX:ami"]));

        Assert.Equal(974, result.Drg);
        Assert.Equal(25, result.Mdc);
        Assert.Equal([0, 25], result.MdcStops.Stops);
    }

    /// <summary>
    /// Two hops are followed, and the second hop's MDC is not recorded — so
    /// the MDC reported is the first destination, not where the DRG came
    /// from. That asymmetry is CMS's, and it is the kind of thing that only
    /// shows up on a remittance.
    /// </summary>
    [Fact]
    public void Two_hops_are_followed_and_only_the_first_is_recorded()
    {
        var table = Table(
            Row(0, 1, 0, "PDX:strauma", reRouteMdc: 24),
            Row(24, 1, 0, "ORPROC", reRouteMdc: 29),
            Row(29, 1, 981, "ORPROC"));

        var result = MsDrgGrouper.Group(
            table, Stay(5, mask: ["PDX:strauma", "ORPROC"]));

        Assert.Equal(981, result.Drg);
        Assert.Equal([0, 24], result.MdcStops.Stops);
        Assert.Equal(24, result.Mdc);
    }

    /// <summary>
    /// A third hop is not followed. CMS looks at the reroute on the second
    /// hop's match and stops, so a row reached on the second hop stands as the
    /// answer even when it carries a reroute of its own.
    /// </summary>
    [Fact]
    public void A_third_hop_is_not_followed()
    {
        var table = Table(
            Row(0, 1, 0, "PDX:strauma", reRouteMdc: 24),
            Row(24, 1, 0, "ORPROC", reRouteMdc: 29),
            Row(29, 1, 981, "ORPROC", reRouteMdc: 11),
            Row(11, 1, 652, "ORPROC"));

        Assert.Equal(981, MsDrgGrouper.Group(
            table, Stay(5, mask: ["PDX:strauma", "ORPROC"])).Drg);
    }

    /// <summary>
    /// When the destination MDC matches nothing, grouping fails rather than
    /// keeping the DRG of the row that rerouted the stay. The alternative
    /// would be to price a claim under a row CMS does not treat as a DRG.
    /// </summary>
    [Fact]
    public void A_reroute_that_matches_nothing_fails_grouping()
    {
        var table = Table(
            Row(0, 1, 0, "PDX:strauma", reRouteMdc: 24),
            Row(24, 1, 957, "ORPROC"));

        var failure = Assert.Throws<MsDrgGroupingFailedException>(
            () => MsDrgGrouper.Group(table, Stay(5, mask: ["PDX:strauma"])));

        Assert.Contains("assigns no DRG", failure.Message);
        Assert.Contains("not rerouted", failure.Message);
    }

    /// <summary>
    /// MDC 29 is a shared destination rather than a body system, so a stay
    /// routed into it reports the MDC it was routed out of.
    /// </summary>
    [Fact]
    public void A_stay_routed_into_the_shared_mdc_reports_where_it_came_from()
    {
        var table = Table(
            Row(0, 1, 0, "MDC00"),
            Row(1, 1, 0, "ORPROC", reRouteMdc: 29),
            Row(29, 1, 981, "ORPROC"));

        var result = MsDrgGrouper.Group(table, Stay(1, mask: ["ORPROC"]));

        Assert.Equal(981, result.Drg);
        Assert.Equal([0, 1, 29], result.MdcStops.Stops);
        Assert.Equal(1, result.Mdc);
    }

    /// <summary>
    /// An edit in the destination MDC refuses the claim just as one in the
    /// pre-MDC pass does.
    /// </summary>
    [Fact]
    public void An_edit_after_a_reroute_refuses_the_claim()
    {
        var table = Table(
            Row(0, 1, 0, "PDX:hiv", reRouteMdc: 25),
            Row(25, 1, 0, "invalid_sex"),
            Row(25, 2, 974, "PDX:hiv"));

        var result = MsDrgGrouper.Group(
            table, Stay(5, mask: ["PDX:hiv", "invalid_sex"]));

        Assert.False(result.Groupable);
        Assert.Equal(4, result.ReturnCode);
    }

    /// <summary>
    /// The severity reported is the last ranking's, not the pre-MDC pass's.
    /// </summary>
    [Fact]
    public void The_severity_reported_is_the_last_rankings()
    {
        var table = Table(
            Row(0, 1, 0, "MDC00"),
            Row(5, 1, 280, "ANY:ami & MCC"));

        var result = MsDrgGrouper.Group(
            table,
            Stay(5, mask: ["PDX:ami"], secondarySeverity: ComplicationLevel.Mcc));

        Assert.Equal(280, result.Drg);
        Assert.Equal(ComplicationLevel.Mcc, result.Severity);
    }

    /// <summary>
    /// A row that names a diagnosis list without a position cannot match, and
    /// V44 holds one: MDC 5 rank 87, <c>(ANY:ami) &amp; (ami)</c>, which
    /// assigns no DRG. A diagnosis's lists only ever enter the mask under
    /// <c>PDX:</c> or <c>SDX:</c>, so the unprefixed half of that formula is
    /// unsatisfiable and the row is dead — which is the only reason a stay
    /// with an AMI reaching rank 87 in MDC 5 does not fail grouping.
    /// </summary>
    [Fact]
    public void A_diagnosis_list_named_without_a_position_cannot_match()
    {
        var table = Table(
            Row(5, 87, 0, "(ANY:ami) & (ami)"),
            Row(5, 88, 280, "ANY:ami"));

        // The dead row is tried and fails; the next row answers.
        Assert.Equal(280, MsDrgGrouper.Group(table, Stay(5, mask: ["PDX:ami"])).Drg);
    }

    /// <summary>
    /// A stay whose principal diagnosis the code table does not recognise has
    /// no MDC, so no ranking exists for it and grouping fails. On a real claim
    /// the pre-MDC edits catch this first; this is what happens when they are
    /// not in the table.
    /// </summary>
    [Fact]
    public void An_unrecognised_principal_diagnosis_fails_grouping()
    {
        var table = Table(Row(5, 1, 280, "ANY:ami"));

        var failure = Assert.Throws<MsDrgGroupingFailedException>(
            () => MsDrgGrouper.Group(table, Stay(-1, mask: ["PDX:ami"])));

        Assert.Contains("No formula matched", failure.Message);
    }
}
