using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;

namespace CloudHealthOffice.GrouperEngine.Severity;

/// <summary>
/// What the secondary diagnoses on a claim are worth in severity.
///
/// <para>
/// <b>Severity is computed per formula, not per claim.</b> That is the part of
/// this step most easily got wrong. CMS walks an MDC's formulas in rank order
/// and, for each one, recomputes the stay's severity with that formula's own
/// suppression list applied, then adds the result to the mask as an operand
/// before evaluating. So the mask the formulas see is not constant, and 56 of
/// V44's 824 formulas carry a suppression list — the pancreas/kidney
/// transplant rule suppresses <c>diabetes</c> and <c>renalfail</c>, because the
/// diabetes and renal failure are the reason for the transplant rather than
/// complications of the stay. A grouper that computed severity once, before
/// the loop, would pay those 56 formulas at the wrong severity.
/// </para>
///
/// <para>
/// Three things remove a secondary from the count, and all three are
/// reductions — leaving any of them out produces a higher-paying DRG, with no
/// error to notice:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     It is on the principal diagnosis's CC exclusion list, so it says
///     nothing the principal did not. See <see cref="IMsDrgExclusionSource"/>.
///   </description></item>
///   <item><description>
///     It is an <c>mccalive</c> code and the patient died, so it counts only
///     on a stay the patient survived.
///   </description></item>
///   <item><description>
///     It is a hospital-acquired condition the hospital is not paid for.
///   </description></item>
/// </list>
/// </summary>
public static class MsDrgSeverityProcessor
{
    /// <summary>
    /// The operand marking a code that is an MCC only when the patient
    /// survived the stay.
    /// </summary>
    public const string MajorComplicationIfAlive = "mccalive";

    /// <summary>
    /// The severity to credit the stay with, for one formula.
    /// </summary>
    /// <param name="codes">
    /// The claim's codes. Only the secondaries count: CMS passes the secondary
    /// list, and a principal diagnosis is never its own complication.
    /// </param>
    /// <param name="excludedSecondaries">
    /// The principal diagnosis's CC exclusion list, from
    /// <see cref="IMsDrgExclusionSource.ExclusionsFor"/>.
    /// </param>
    /// <param name="patientDied">
    /// Whether the discharge status says the patient died, which is what makes
    /// an <c>mccalive</c> code stop counting.
    /// </param>
    /// <param name="hospitalAcquiredConditions">
    /// Secondary codes the HAC policy has taken out of the count. Nothing in
    /// this engine computes these yet; a caller that cannot supply them must
    /// refuse the claim rather than pass an empty set, because the result
    /// would be a higher severity than the stay has earned.
    /// </param>
    /// <param name="severitySuppressionOperands">
    /// The formula's own suppression lists. A secondary on any of them does
    /// not count for <i>this</i> formula. CMS's data writes an empty string
    /// where a formula suppresses nothing, which matches no list and so
    /// suppresses nothing — no special case is needed and none is made.
    /// </param>
    public static ComplicationLevel ForFormula(
        ClaimCodes codes,
        IReadOnlySet<string> excludedSecondaries,
        bool patientDied,
        IReadOnlySet<string> hospitalAcquiredConditions,
        IEnumerable<string> severitySuppressionOperands)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(excludedSecondaries);
        ArgumentNullException.ThrowIfNull(hospitalAcquiredConditions);
        ArgumentNullException.ThrowIfNull(severitySuppressionOperands);

        var suppression = severitySuppressionOperands as IReadOnlyCollection<string>
            ?? [.. severitySuppressionOperands];

        // CMS reduces from NON_CC, so a claim with no counting secondary is
        // NON_CC and not "no severity". CHO's model has one value for both,
        // which is safe only because no V44 formula tests either: the formulas
        // test MCC and CC and nothing else.
        var highest = ComplicationLevel.None;

        // Skip(1): the principal diagnosis. The mask builder puts it first.
        foreach (var secondary in codes.Diagnoses.Skip(1))
        {
            if (hospitalAcquiredConditions.Contains(secondary.Code)
                || excludedSecondaries.Contains(secondary.Code)
                || (patientDied && secondary.HasOperand(MajorComplicationIfAlive))
                || IsSuppressed(secondary, suppression))
            {
                continue;
            }

            if (secondary.Severity > highest)
            {
                highest = secondary.Severity;
            }
        }

        return highest;
    }

    /// <summary>
    /// The operand the severity enters the mask as. CMS adds the severity
    /// enum's own name, so the operands are <c>MCC</c> and <c>CC</c> —
    /// which is exactly what the formulas test — and <c>NON_CC</c>, which no
    /// formula tests and which therefore matches nothing.
    /// </summary>
    public static OperandName Operand(ComplicationLevel severity) =>
        new(OperandPosition.Unqualified, severity switch
        {
            ComplicationLevel.Mcc => "MCC",
            ComplicationLevel.Cc => "CC",
            _ => "NON_CC",
        });

    private static bool IsSuppressed(
        ClaimDiagnosis secondary, IReadOnlyCollection<string> suppression)
    {
        foreach (var operand in suppression)
        {
            if (secondary.HasOperand(operand))
            {
                return true;
            }
        }

        return false;
    }
}
