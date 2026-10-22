using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Severity;

namespace CloudHealthOffice.GrouperEngine.Grouping;

/// <summary>
/// A stay as both grouping passes and the HAC policy between them see it.
/// </summary>
/// <param name="Mask">The stay's mask, without a severity operand.</param>
/// <param name="Codes">The claim's codes, principal diagnosis first.</param>
/// <param name="ExcludedSecondaries">The principal diagnosis's CC exclusion list.</param>
/// <param name="PatientDied">
/// Whether the discharge status says the patient died, which is what stops an
/// <c>mccalive</c> secondary counting.
/// </param>
/// <param name="Secondaries">
/// The secondary diagnoses with their POA indicators, in the same order as
/// <paramref name="Codes"/>'s diagnoses after the principal. The two are
/// checked against each other, because a POA indicator attached to the wrong
/// code decides the wrong condition.
/// </param>
/// <param name="Reporting">Whether the facility reports POA.</param>
public sealed record MsDrgHacStay(
    AttributeMask Mask,
    ClaimCodes Codes,
    IReadOnlySet<string> ExcludedSecondaries,
    bool PatientDied,
    IReadOnlyList<MsDrgHacSecondary> Secondaries,
    HospitalPoaReporting Reporting);

/// <summary>
/// What a stay groups to before and after the hospital-acquired-condition
/// policy is applied.
/// </summary>
/// <param name="BeforeHacPolicy">
/// The DRG the stay groups to with every secondary counting. This is what the
/// hospital would be paid if it had caused none of them, and CMS reports it
/// too — the pair is the whole of the HAC payment mechanism, and a result that
/// showed only one of them could not explain the difference.
/// </param>
/// <param name="AfterHacPolicy">
/// The DRG the stay is paid: the same grouping with the conditions the hospital
/// caused taken out of the severity count, or a refusal when the POA
/// indicators cannot carry the decision.
/// </param>
/// <param name="Hac">What the policy decided, per secondary diagnosis.</param>
public sealed record MsDrgPaidGrouping(
    MsDrgGrouping BeforeHacPolicy,
    MsDrgGrouping AfterHacPolicy,
    MsDrgHacDetermination Hac)
{
    /// <summary>
    /// Whether the policy changed the DRG. False on the great majority of
    /// stays, and the reason the two passes exist on all of them.
    /// </summary>
    public bool PolicyChangedTheDrg => BeforeHacPolicy.Drg != AfterHacPolicy.Drg;
}

/// <summary>
/// Groups a stay the way CMS does: twice, with the
/// hospital-acquired-condition policy in between.
///
/// <para>
/// The second pass is not a refinement of the first. A condition the hospital
/// caused stops counting towards the stay's complication level, which can drop
/// the stay out of a severity-split DRG and into its lower-paying sibling —
/// that is how Medicare declines to pay for harm it attributes to the
/// hospital. So the DRG is decided twice on every stay, and both answers are
/// part of the result.
/// </para>
///
/// <para>
/// CMS's chain is: preprocessing, initial grouping, initial marking, the HAC
/// processor, final grouping, final marking. The first grouping pass runs with
/// no conditions determined, which is not an approximation — CMS's severity
/// filter asks each code whether its condition's criteria were met, and before
/// the HAC processor has run, none have.
/// </para>
///
/// <para>
/// <b>What this does not do.</b> Marking: CMS's two marking passes record
/// which codes affected the DRG, which is what an explanation of a paid amount
/// is made of. <see cref="MsDrgGrouping.MatchedOperands"/> carries the
/// operands that earned the DRG, which is most of the same information from
/// the other direction.
/// </para>
/// </summary>
public static class MsDrgHacAwareGrouper
{
    /// <summary>
    /// Groups <paramref name="stay"/>, applies the HAC policy, and groups it
    /// again.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The POA indicators do not line up with the claim's secondary diagnoses.
    /// </exception>
    /// <exception cref="MsDrgGroupingFailedException">
    /// Grouping finished without a DRG.
    /// </exception>
    public static MsDrgPaidGrouping Group(
        IMsDrgFormulaSource formulas, IMsDrgHacSource hacs, MsDrgHacStay stay)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(hacs);
        ArgumentNullException.ThrowIfNull(stay);

        var secondaries = stay.Codes.Diagnoses.Count == 0
            ? 0
            : stay.Codes.Diagnoses.Count - 1;

        if (stay.Secondaries.Count != secondaries)
        {
            throw new ArgumentException(
                $"The stay carries {secondaries} secondary diagnoses and "
                + $"{stay.Secondaries.Count} POA indicators. Pairing them up by "
                + "position would decide the wrong conditions.",
                nameof(stay));
        }

        for (var index = 0; index < secondaries; index++)
        {
            var code = stay.Codes.Diagnoses[index + 1].Code;
            var reported = stay.Secondaries[index].Code;

            if (!string.Equals(code, reported, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"POA indicator {index + 1} is reported for '{reported}' and the "
                    + $"claim's secondary diagnosis {index + 1} is '{code}'.",
                    nameof(stay));
            }
        }

        // Nothing is determined yet, so nothing is taken out of the count.
        var before = MsDrgGrouper.Group(
            formulas,
            new MsDrgStay(
                stay.Mask,
                stay.Codes,
                stay.ExcludedSecondaries,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                stay.PatientDied));

        var determination = MsDrgHacProcessor.Determine(
            stay.Secondaries,
            stay.Codes,
            stay.Mask,
            stay.ExcludedSecondaries,
            hacs,
            stay.Reporting);

        if (determination.ReturnCode is int refused)
        {
            // CMS stops the chain here: the stay keeps the DRG it grouped to
            // before the policy ran, and what it is paid is nothing until the
            // POA indicators are corrected and the claim resubmitted.
            return new MsDrgPaidGrouping(
                before,
                MsDrgGrouper.Refuse(
                    refused, before.Severity, before.MdcStops),
                determination);
        }

        var after = MsDrgGrouper.Group(
            formulas,
            new MsDrgStay(
                stay.Mask,
                stay.Codes,
                stay.ExcludedSecondaries,
                determination.RemovedFromSeverity,
                stay.PatientDied));

        return new MsDrgPaidGrouping(before, after, determination);
    }
}
