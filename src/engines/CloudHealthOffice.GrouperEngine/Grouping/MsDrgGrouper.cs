using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;

namespace CloudHealthOffice.GrouperEngine.Grouping;

/// <summary>
/// Where a stay stopped on its way to a DRG: the MDCs whose rankings were
/// walked, in order.
///
/// <para>
/// This is not bookkeeping. CMS reports the MDC off this list rather than off
/// the stay's principal diagnosis, so the list is part of the answer.
/// </para>
/// </summary>
/// <param name="Stops">The MDCs walked, in order, starting with the pre-MDC pass.</param>
public sealed record MsDrgMdcStops(IReadOnlyList<int> Stops)
{
    /// <summary>The MDC CMS reports for the stay.</summary>
    /// <param name="principalDiagnosisMdc">
    /// The principal diagnosis's own MDC, which is the answer when the only
    /// stop was the pre-MDC pass.
    /// </param>
    public int Reported(int principalDiagnosisMdc)
    {
        if (Stops.Count == 0)
        {
            return principalDiagnosisMdc;
        }

        var last = Stops[^1];

        // MDC 29 is a shared destination rather than a body system, so CMS
        // reports the MDC the stay was routed out of instead.
        if (last == MdcSharedDestination)
        {
            if (Stops.Contains(MdcMultipleTrauma))
            {
                return MdcMultipleTrauma;
            }

            return Stops.Contains(MdcHivInfection)
                ? MdcHivInfection
                : Stops[^2];
        }

        return last == MsDrgGrouper.PreMdc ? principalDiagnosisMdc : last;
    }

    private const int MdcMultipleTrauma = 24;
    private const int MdcHivInfection = 25;
    private const int MdcSharedDestination = 29;
}

/// <summary>
/// What grouping a stay produced.
/// </summary>
/// <param name="Drg">
/// The DRG, or <see cref="MsDrgGrouper.Ungroupable"/> (999) when an MCE edit
/// stopped grouping.
/// </param>
/// <param name="BaseDrg">The DRG before the severity split.</param>
/// <param name="Mdc">The MDC CMS reports, which is not always the principal diagnosis's.</param>
/// <param name="Severity">
/// The severity the <i>last</i> ranking walked computed. CMS carries the
/// severity forward stage by stage and reports the final one, so a reroute
/// overwrites what the pre-MDC pass computed.
/// </param>
/// <param name="Partition">Medical, surgical, or neither.</param>
/// <param name="ReturnCode">
/// The grouper return code an MCE edit raised, or <see langword="null"/> when
/// none did.
/// </param>
/// <param name="Matched">The row that produced the DRG.</param>
/// <param name="MatchedOperands">
/// The operands that tested true on that row, in evaluation order — the
/// explanation of the DRG.
/// </param>
/// <param name="MdcStops">The MDCs whose rankings were walked.</param>
public sealed record MsDrgGrouping(
    int Drg,
    int BaseDrg,
    int Mdc,
    ComplicationLevel Severity,
    DrgTablePartition Partition,
    int? ReturnCode,
    MsDrgFormulaRow? Matched,
    IReadOnlyList<string> MatchedOperands,
    MsDrgMdcStops MdcStops)
{
    /// <summary>Whether the stay grouped to a real DRG.</summary>
    public bool Groupable => Drg != MsDrgGrouper.Ungroupable;
}

/// <summary>
/// Raised when grouping finished without a DRG.
///
/// <para>
/// CMS throws here too, and it is worth keeping rather than softening into a
/// null DRG: it means a row matched that assigns no DRG and nothing rerouted
/// the stay afterwards, or that no row matched at all. Either way the table
/// and the claim disagree about something, and a priced claim is the wrong
/// place to find that out.
/// </para>
/// </summary>
public sealed class MsDrgGroupingFailedException(string message) : Exception(message);

/// <summary>
/// Decides which MDC a stay belongs to, and groups it.
///
/// <para>
/// <b>MDC assignment is not a search.</b> This was the plan's biggest wrong
/// assumption, written into ADR 024 before CMS's own chain was read: the
/// <c>MDCnn</c> rows in the DRG logic table look like MDC assignment and are
/// not. CMS takes the MDC straight off the principal diagnosis's row in the
/// code table (<c>PdxMdcGrouping</c> is one line:
/// <c>processingData.getPrincipalDx().getMdc()</c>), and no diagnosis or
/// procedure code in V44 carries an <c>MDCnn</c> operand, so those rows cannot
/// match. They are vestigial.
/// </para>
///
/// <para>
/// What does happen is three passes over rankings, in this order:
/// </para>
///
/// <list type="number">
///   <item><description>
///     <b>Pre-MDC.</b> MDC 0's ranking runs first, on every stay. It holds the
///     transplant DRGs, the 7 MCE edit rows, and 3 rows that reroute a stay
///     out to multiple trauma (24), HIV (25) or kidney transplant (11).
///   </description></item>
///   <item><description>
///     <b>The principal diagnosis's MDC</b> — but only if the pre-MDC pass
///     matched nothing. A pre-MDC match, even one assigning no DRG, skips this
///     pass entirely.
///   </description></item>
///   <item><description>
///     <b>Re-routing</b>, at most two hops, from whichever of those matched.
///   </description></item>
/// </list>
///
/// <para>
/// <b>What this does not do.</b> Hospital-acquired conditions. CMS groups
/// twice — once before the HAC rules run and once after — because a HAC
/// takes a secondary diagnosis out of the severity count, which can change
/// the DRG. This runs the single pass, and takes the HAC codes as given on
/// <see cref="MsDrgStay.HospitalAcquiredConditions"/>. Nothing in this engine
/// computes them yet, so a caller passing an empty set is claiming the stay
/// had none. That is a declared gap in ADR 024, not an oversight.
/// </para>
///
/// <para>
/// <b>Confidence.</b> The 11 code-driven operands and the severity count were
/// each built against answers printed by CMS's own classes. This chain was
/// not: it is read off <c>GrouperChain</c>, <c>PreMdcGrouping</c>,
/// <c>PdxMdcGrouping</c>, <c>MdcRerouteGrouping</c> and <c>MsdrgDrgResults</c>
/// and asserted against hand-built tables. The end-to-end parity run is what
/// will confirm the order, and until it has run this step is the least
/// evidenced part of the grouper.
/// </para>
/// </summary>
public static class MsDrgGrouper
{
    /// <summary>The ranking that runs before any MDC is chosen.</summary>
    public const int PreMdc = 0;

    /// <summary>CMS's DRG for a claim it refused to group.</summary>
    public const int Ungroupable = 999;

    /// <summary>Groups a stay.</summary>
    /// <exception cref="MsDrgGroupingFailedException">
    /// Grouping finished without a DRG.
    /// </exception>
    public static MsDrgGrouping Group(IMsDrgFormulaSource formulas, MsDrgStay stay)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(stay);

        // -1 when the claim carries no principal diagnosis, or one the code
        // table does not recognise. No ranking exists for it, so the pass
        // below matches nothing and grouping fails — which is the honest
        // answer, and the edits in the pre-MDC pass normally catch it first.
        var principalMdc = stay.Codes.Diagnoses.Count == 0
            ? -1
            : stay.Codes.Diagnoses[0].Mdc;

        var stops = new List<int> { PreMdc };
        var pre = MsDrgMdcEvaluator.EvaluateMdc(PreMdc, formulas, stay);
        var severity = pre.Severity;

        if (pre.ReturnCode is int preEdit)
        {
            return Refused(preEdit, severity, stops);
        }

        var match = pre.Matched;
        var matchedOperands = pre.MatchedOperands;
        var rerouted = false;

        if (match is null)
        {
            // The principal diagnosis's MDC. Only reached because the pre-MDC
            // pass matched nothing.
            var pdx = MsDrgMdcEvaluator.EvaluateMdc(principalMdc, formulas, stay);
            stops.Add(principalMdc);
            severity = pdx.Severity;

            if (pdx.ReturnCode is int pdxEdit)
            {
                return Refused(pdxEdit, severity, stops);
            }

            match = pdx.Matched;
            matchedOperands = pdx.MatchedOperands;
        }

        if (match is { ReRouteMdc: not 0 })
        {
            // The first hop's MDC is recorded; a second hop's is not. That is
            // CMS's own asymmetry and it is load-bearing, because the reported
            // MDC is read off the end of this list.
            stops.Add(match.ReRouteMdc);

            var hop = MsDrgMdcEvaluator.EvaluateMdc(match.ReRouteMdc, formulas, stay);
            severity = hop.Severity;

            if (hop.ReturnCode is int hopEdit)
            {
                return Refused(hopEdit, severity, stops);
            }

            if (hop.Matched is { ReRouteMdc: not 0 } second)
            {
                hop = MsDrgMdcEvaluator.EvaluateMdc(second.ReRouteMdc, formulas, stay);
                severity = hop.Severity;

                if (hop.ReturnCode is int secondEdit)
                {
                    return Refused(secondEdit, severity, stops);
                }
            }

            // A hop that matched nothing leaves the stay on the match it was
            // rerouted from, carrying that row's DRG of 0 — so grouping fails
            // below rather than silently keeping the pre-reroute DRG.
            if (hop.Matched is not null)
            {
                match = hop.Matched;
                matchedOperands = hop.MatchedOperands;
                rerouted = true;
            }
        }

        var mdcStops = new MsDrgMdcStops(stops);

        if (match is null)
        {
            throw new MsDrgGroupingFailedException(
                $"No formula matched for a stay whose principal diagnosis is in MDC "
                + $"{principalMdc}; MDCs walked: {string.Join(", ", stops)}.");
        }

        if (match.Drg == 0)
        {
            throw new MsDrgGroupingFailedException(
                $"MDC {match.Mdc} rank {match.Rank} matched and assigns no DRG "
                + $"(formula '{match.FormulaText}'); MDCs walked: "
                + $"{string.Join(", ", stops)}."
                + (rerouted ? string.Empty : " The stay was not rerouted."));
        }

        return new MsDrgGrouping(
            match.Drg,
            match.BaseDrg,
            mdcStops.Reported(principalMdc),
            severity,
            match.Partition,
            ReturnCode: null,
            match,
            matchedOperands,
            mdcStops);
    }

    /// <summary>
    /// The grouping of a claim something refused: DRG 999, no partition, and
    /// the return code that refused it.
    /// </summary>
    public static MsDrgGrouping Refuse(
        int returnCode, ComplicationLevel severity, MsDrgMdcStops stops) =>
        new(
            Ungroupable,
            Ungroupable,
            Mdc: 0,
            severity,
            DrgTablePartition.NotApplicable,
            returnCode,
            Matched: null,
            MatchedOperands: [],
            stops);

    private static MsDrgGrouping Refused(
        int returnCode, ComplicationLevel severity, List<int> stops) =>
        new(
            Ungroupable,
            Ungroupable,
            Mdc: 0,
            severity,
            DrgTablePartition.NotApplicable,
            returnCode,
            Matched: null,
            MatchedOperands: [],
            new MsDrgMdcStops(stops));
}
