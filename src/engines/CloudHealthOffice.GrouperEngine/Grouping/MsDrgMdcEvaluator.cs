using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Severity;

namespace CloudHealthOffice.GrouperEngine.Grouping;

/// <summary>
/// What the stay looks like to an MDC's ranked formulas.
/// </summary>
/// <param name="Mask">
/// The stay's mask <i>without</i> a severity operand. The evaluator adds that
/// per row, because each row computes its own severity.
/// </param>
/// <param name="Codes">
/// The claim's codes, principal diagnosis first, which the code-driven
/// operands and the severity count read.
/// </param>
/// <param name="ExcludedSecondaries">
/// The principal diagnosis's CC exclusion list, from
/// <see cref="IMsDrgExclusionSource.ExclusionsFor"/>.
/// </param>
/// <param name="HospitalAcquiredConditions">
/// The secondary codes the HAC policy has taken out of the severity count.
/// There is no default: nothing in this engine computes them yet, and an
/// empty set is a claim's worth of severity the stay has not earned rather
/// than a neutral starting point.
/// </param>
/// <param name="PatientDied">
/// Whether the discharge status says the patient died, which is what stops an
/// <c>mccalive</c> secondary counting.
/// </param>
public sealed record MsDrgStay(
    AttributeMask Mask,
    ClaimCodes Codes,
    IReadOnlySet<string> ExcludedSecondaries,
    IReadOnlySet<string> HospitalAcquiredConditions,
    bool PatientDied);

/// <summary>
/// The result of walking one MDC's ranking.
/// </summary>
/// <param name="Matched">
/// The first row whose formula matched, or <see langword="null"/> if none did.
/// </param>
/// <param name="Severity">
/// The severity computed for the matched row — or, if nothing matched, for the
/// last row tried. The second half of that is CMS's behaviour and not an
/// obviously intended one, so a caller should read this only alongside a
/// match.
/// </param>
/// <param name="MatchedOperands">
/// The operands that tested true on the matched row, in evaluation order.
/// This is what makes the DRG explainable: the number alone does not say which
/// diagnosis or procedure earned it, and that is what an appeal turns on.
/// </param>
/// <param name="ReturnCode">
/// The grouper return code an MCE edit raised, or <see langword="null"/>. A
/// non-null value is how a claim becomes ungroupable, and the boolean match
/// does not carry it.
/// </param>
/// <param name="RowsTried">How far down the ranking the evaluation went.</param>
public sealed record MsDrgMdcEvaluation(
    MsDrgFormulaRow? Matched,
    ComplicationLevel Severity,
    IReadOnlyList<string> MatchedOperands,
    int? ReturnCode,
    int RowsTried);

/// <summary>
/// Walks an MDC's ranked formulas and returns the first that matches.
///
/// <para>
/// This is the whole of MS-DRG's branching logic, and it is a loop rather than
/// a decision tree: the rows are tried in rank order and the first match wins.
/// Two things about the loop are easy to get wrong and quiet when wrong:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     <b>The mask is not constant.</b> Severity is recomputed for every row,
///     with that row's own suppression list applied, and added to the mask as
///     an operand before the row is evaluated. 56 of V44's 824 rows carry a
///     suppression list. A grouper that computed severity once would pay those
///     56 at the wrong severity.
///   </description></item>
///   <item><description>
///     <b>Rank ties keep the table's order.</b> CMS sorts by rank with a
///     stable sort, and in 14 MDCs an <c>MDCnn</c> row shares a rank with a
///     real DRG row.
///   </description></item>
/// </list>
///
/// <para>
/// <b>What this does not do.</b> It takes the MDC as given. Deciding which MDC
/// a stay belongs to — the pre-MDC rules, the <c>MDCnn</c> rows, and the
/// re-routing that <see cref="MsDrgFormulaRow.ReRouteMdc"/> carries on 20 V44
/// rows — is a step above this one and is not built. So this is the engine's
/// branching logic, not yet a grouper: a caller has to know the MDC, and
/// nothing here will tell it that the MDC it chose was the wrong one.
/// </para>
/// </summary>
public static class MsDrgMdcEvaluator
{
    /// <summary>
    /// Tries <paramref name="mdc"/>'s rows in rank order and returns the first
    /// match.
    /// </summary>
    public static MsDrgMdcEvaluation EvaluateMdc(
        int mdc, IMsDrgFormulaSource formulas, MsDrgStay stay)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(stay);

        var severity = ComplicationLevel.None;
        var tried = 0;

        foreach (var row in formulas.ForMdc(mdc))
        {
            tried++;

            severity = MsDrgSeverityProcessor.ForFormula(
                stay.Codes,
                stay.ExcludedSecondaries,
                stay.PatientDied,
                stay.HospitalAcquiredConditions,
                row.SeveritySuppressionOperands);

            // CMS builds a fresh mask per row rather than adding and removing
            // the severity operand, and so does this: a mask mutated in place
            // would leak one row's severity into the next.
            var mask = new AttributeMask(stay.Mask);
            mask.Add(MsDrgSeverityProcessor.Operand(severity));

            var matched = new List<string>();
            var returnCodes = new GrouperReturnCodeSink();

            var satisfied = row.Formula.Evaluate(
                new DrgFormulaEvaluation(
                    mask,
                    Mdc: mdc,
                    ApplySuppression: true,
                    ReturnCodes: returnCodes,
                    Claim: stay.Codes),
                matched);

            if (satisfied)
            {
                return new MsDrgMdcEvaluation(
                    row, severity, matched, returnCodes.Last, tried);
            }
        }

        // The severity reported here is the last row's, which is CMS's
        // behaviour: it keeps the variable across the loop and returns it
        // whether or not anything matched.
        return new MsDrgMdcEvaluation(null, severity, [], null, tried);
    }
}
