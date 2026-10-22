using CloudHealthOffice.GrouperEngine.Formulas;

namespace CloudHealthOffice.GrouperEngine.Hac;

/// <summary>
/// A secondary diagnosis as the HAC policy reads it.
/// </summary>
/// <param name="Code">The diagnosis code.</param>
/// <param name="Poa">What the facility reported for present-on-admission.</param>
public sealed record MsDrgHacSecondary(string Code, PresentOnAdmission Poa);

/// <summary>What the HAC policy decided about one secondary diagnosis.</summary>
/// <param name="Code">The code.</param>
/// <param name="Conditions">
/// Each HAC list the code is on, with what was decided about it.
/// </param>
public sealed record MsDrgHacOutcome(
    string Code,
    IReadOnlyList<(MsDrgHacEntry Entry, MsDrgHacStatus Status)> Conditions)
{
    /// <summary>Whether any condition on this code was acquired in hospital.</summary>
    public bool AnyCriteriaMet =>
        Conditions.Any(condition => condition.Status == MsDrgHacStatus.CriteriaMet);
}

/// <summary>What the HAC policy decided about a stay.</summary>
/// <param name="Outcomes">
/// One entry per secondary diagnosis passed in, in the same order.
/// </param>
/// <param name="RemovedFromSeverity">
/// The codes that stop counting towards severity. This is what
/// <c>MsDrgStay.HospitalAcquiredConditions</c> wants.
/// </param>
/// <param name="ReturnCode">
/// The grouper return code that refuses the claim, or <see langword="null"/>.
/// A refusal here is about the POA indicators, not the clinical facts: CMS
/// will not price a stay carrying a hospital-acquired condition whose POA it
/// cannot read.
/// </param>
/// <param name="ProcedureConditions">
/// The HAC numbers a procedure on the claim contributed to, for the audit
/// record. Six of the 14 conditions need a procedure.
/// </param>
public sealed record MsDrgHacDetermination(
    IReadOnlyList<MsDrgHacOutcome> Outcomes,
    IReadOnlySet<string> RemovedFromSeverity,
    int? ReturnCode,
    IReadOnlySet<int> ProcedureConditions);

/// <summary>
/// Decides which of a stay's secondary diagnoses were acquired in hospital.
///
/// <para>
/// This is the third of the three reductions to the severity count, and like
/// the other two it only ever pays <i>less</i>: a hospital-acquired condition
/// is one Medicare will not pay the hospital for having caused, so it stops
/// counting towards the stay's complication level. Leaving it out pays more
/// than the claim has earned, which is why the severity processor takes the
/// set as a parameter rather than defaulting it to empty.
/// </para>
///
/// <para>
/// <b>Three things here are CMS's behaviour rather than the policy's, and all
/// three are replicated deliberately.</b> Parity is the requirement: a grouper
/// that implements the policy as written disagrees with the payer, and
/// disagreeing in this direction underpays the provider.
/// </para>
///
/// <list type="number">
///   <item><description>
///     <b>HAC 6 cannot fire in V44.</b> A code only gets HAC entries at all if
///     one of its HAC list names starts with an upper-case <c>HAC</c> —
///     CMS's check is <c>attr.getListName().startsWith("HAC")</c>, and Java's
///     is case-sensitive. The only V44 codes on HAC 6 are the 14 on
///     <c>hac06_show</c> and the 2 on <c>hac06_sdx</c>, and not one of them
///     carries an upper-case <c>HACnn</c> list. So catheter-associated urinary
///     tract infection is never determined, its formula never evaluated, and
///     its show-list rule never reached. Two of the nine <c>hac11_sdx</c> codes
///     are excluded by the same gate.
///   </description></item>
///   <item><description>
///     <b>The unknown-reporting-status check is a tautology.</b> CMS counts
///     secondaries where <c>poa != Y || poa != W</c>, which is true of every
///     code, so the count is simply how many secondaries the claim has. Two or
///     more, on a stay with any condition whose criteria were met and a
///     facility whose reporting status is unknown, refuses the claim. Written
///     out as <see cref="SecondariesCountedByCmssTautology"/> so it is
///     impossible to read this as arithmetic that was meant.
///   </description></item>
///   <item><description>
///     <b>An unreadable POA anywhere refuses the claim</b>, not only on the
///     code carrying the condition. <c>hasInvalidPoaOnClaim</c> is set while
///     walking every HAC-bearing secondary and then applied to every condition
///     except HAC 6, so one blank indicator on an unrelated HAC-list code
///     refuses a stay whose own indicators are complete.
///   </description></item>
/// </list>
///
/// <para>
/// <b>What this does not do.</b> CMS groups twice — once before this runs and
/// once after — because a condition taken out of the severity count can change
/// the DRG. This decides the conditions; running the two passes around it is
/// the caller's job and is not yet wired into <c>MsDrgGrouper</c>.
/// </para>
/// </summary>
public static class MsDrgHacProcessor
{
    /// <summary>CMS's refusal when a condition's POA cannot be read.</summary>
    public const int MissingOnePoa = 9;

    /// <summary>
    /// CMS's refusal when the facility's reporting status is unknown and a
    /// condition's POA is <c>N</c> or <c>U</c>.
    /// </summary>
    public const int UnknownStatusPoaNotPresent = 10;

    /// <summary>
    /// CMS's refusal when the facility's reporting status is unknown and a
    /// condition's POA cannot be read.
    /// </summary>
    public const int UnknownStatusPoaUnreadable = 11;

    /// <summary>
    /// CMS's refusal when the facility's reporting status is unknown and the
    /// claim has more than one secondary diagnosis. See the tautology above:
    /// the condition reads as though it were about POA values, and is not.
    /// </summary>
    public const int UnknownStatusMultipleConditions = 15;

    private const int CatheterAssociatedUti = 6;
    private const string ShowList = "hac06_show";

    /// <summary>
    /// The HAC numbers whose determination needs a procedure on the claim.
    /// </summary>
    public static IReadOnlySet<int> ConditionsNeedingAProcedure { get; } =
        new HashSet<int> { 8, 10, 11, 12, 13, 14 };

    /// <summary>
    /// How many secondaries CMS counts for
    /// <see cref="UnknownStatusMultipleConditions"/>: all of them. CMS's
    /// condition is <c>poa != Y || poa != W</c>, which no value fails.
    /// </summary>
    public static int SecondariesCountedByCmssTautology(
        IReadOnlyList<MsDrgHacSecondary> secondaries) => secondaries.Count;

    /// <summary>
    /// Decides the stay's hospital-acquired conditions.
    /// </summary>
    /// <param name="secondaries">
    /// The secondary diagnoses with their POA indicators, in claim order. The
    /// principal diagnosis is not among them: CMS's HAC processor walks the
    /// secondaries only.
    /// </param>
    /// <param name="codes">
    /// The claim's codes as the mask builder produced them, principal first.
    /// A HAC formula is evaluated against the secondaries only, which is CMS's
    /// context and matters for any operand that counts codes.
    /// </param>
    /// <param name="mask">The stay's mask, without a severity operand.</param>
    /// <param name="excludedSecondaries">
    /// The principal diagnosis's CC exclusion list. A code already excluded by
    /// it is not put through the HAC rules at all.
    /// </param>
    /// <param name="hacs">CMS's HAC tables.</param>
    /// <param name="reporting">Whether the facility reports POA.</param>
    public static MsDrgHacDetermination Determine(
        IReadOnlyList<MsDrgHacSecondary> secondaries,
        ClaimCodes codes,
        AttributeMask mask,
        IReadOnlySet<string> excludedSecondaries,
        IMsDrgHacSource hacs,
        HospitalPoaReporting reporting)
    {
        ArgumentNullException.ThrowIfNull(secondaries);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(excludedSecondaries);
        ArgumentNullException.ThrowIfNull(hacs);

        // The formulas see the secondaries only, as CMS's HAC context does.
        var hacCodes = new ClaimCodes(
            [.. codes.Diagnoses.Skip(1)],
            codes.Procedures);

        var outcomes = new List<MsDrgHacOutcome>(secondaries.Count);
        var onAHacList = new List<MsDrgHacSecondary>();
        var met = new Dictionary<int, List<MsDrgHacSecondary>>();
        var procedureConditions = new HashSet<int>();
        string? conditionSixOffTheShowList = null;
        var anyUnreadablePoa = false;

        foreach (var secondary in secondaries)
        {
            var entries = hacs.EntriesFor(secondary.Code);

            if (entries.Count > 0)
            {
                onAHacList.Add(secondary);
            }

            var conditions =
                new List<(MsDrgHacEntry Entry, MsDrgHacStatus Status)>(entries.Count);

            foreach (var entry in entries)
            {
                MsDrgHacStatus status;

                if (reporting == HospitalPoaReporting.Exempt)
                {
                    // The facility does not report POA, so nothing on the
                    // claim can be shown to have been acquired in hospital.
                    status = MsDrgHacStatus.NotApplicableExempt;
                }
                else if (WasPresentOnAdmission(secondary.Poa))
                {
                    status = MsDrgHacStatus.CriteriaNotMet;
                }
                else if (excludedSecondaries.Contains(secondary.Code))
                {
                    status = MsDrgHacStatus.NotApplicableExclusion;
                }
                else
                {
                    status = Satisfied(entry.Number, hacs, mask, hacCodes)
                        ? MsDrgHacStatus.CriteriaMet
                        : MsDrgHacStatus.CriteriaNotMet;

                    // Set while walking every HAC-bearing secondary, whether
                    // or not this one's condition was met.
                    if (IsUnreadable(secondary.Poa))
                    {
                        anyUnreadablePoa = true;
                    }

                    if (status == MsDrgHacStatus.CriteriaMet)
                    {
                        if (entry.Number == CatheterAssociatedUti
                            && !IsOnShowList(entries))
                        {
                            conditionSixOffTheShowList = secondary.Code;
                        }

                        if (!met.TryGetValue(entry.Number, out var carrying))
                        {
                            carrying = [];
                            met[entry.Number] = carrying;
                        }

                        carrying.Add(secondary);
                    }
                }

                conditions.Add((entry, status));

                if (ConditionsNeedingAProcedure.Contains(entry.Number))
                {
                    RecordProcedures(entry.Number, codes, procedureConditions);
                }
            }

            outcomes.Add(new MsDrgHacOutcome(secondary.Code, conditions));
        }

        // With nothing on HAC 6 off the show list, the show-list entries are
        // not conditions. Unreachable in V44 for the reason in the class
        // documentation, and kept so a fiscal year that un-gates HAC 6 does
        // not need this rediscovered.
        if (conditionSixOffTheShowList is null)
        {
            outcomes = [.. outcomes.Select(ClearShowListConditions)];
            met.Remove(CatheterAssociatedUti);
        }

        var returnCode = Refusal(
            secondaries,
            onAHacList,
            met,
            reporting,
            conditionSixOffTheShowList,
            anyUnreadablePoa);

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (reporting != HospitalPoaReporting.Exempt)
        {
            foreach (var outcome in outcomes.Where(outcome => outcome.AnyCriteriaMet))
            {
                removed.Add(outcome.Code);
            }
        }

        return new MsDrgHacDetermination(
            outcomes, removed, returnCode, procedureConditions);
    }

    /// <summary>
    /// Whether the POA indicator says the condition was there on arrival.
    /// <c>W</c> counts as yes: clinically undetermined is not the hospital's
    /// fault.
    /// </summary>
    public static bool WasPresentOnAdmission(PresentOnAdmission poa) =>
        poa is PresentOnAdmission.Yes or PresentOnAdmission.ClinicallyUndetermined;

    /// <summary>
    /// Whether the POA indicator cannot be read. All four of these behave
    /// identically in every rule CMS applies.
    /// </summary>
    public static bool IsUnreadable(PresentOnAdmission poa) =>
        poa is PresentOnAdmission.ExemptE
            or PresentOnAdmission.Blank
            or PresentOnAdmission.ExemptOne
            or PresentOnAdmission.Invalid;

    private static bool IsOnShowList(IReadOnlyList<MsDrgHacEntry> entries) =>
        entries.Any(entry =>
            string.Equals(entry.ListName, ShowList, StringComparison.OrdinalIgnoreCase));

    private static MsDrgHacOutcome ClearShowListConditions(MsDrgHacOutcome outcome) =>
        new(
            outcome.Code,
            [.. outcome.Conditions.Select(condition =>
                string.Equals(
                    condition.Entry.ListName, ShowList, StringComparison.OrdinalIgnoreCase)
                    ? (condition.Entry, MsDrgHacStatus.CriteriaNotMet)
                    : condition)]);

    private static bool Satisfied(
        int hacNumber, IMsDrgHacSource hacs, AttributeMask mask, ClaimCodes codes)
    {
        foreach (var formula in hacs.FormulasFor(hacNumber))
        {
            // No suppression and no MDC: a HAC formula is evaluated outside any
            // MDC, so a procedure a cluster claimed still answers here.
            if (formula.Evaluate(
                new DrgFormulaEvaluation(
                    mask, Mdc: 0, ApplySuppression: false, Claim: codes)))
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordProcedures(
        int hacNumber, ClaimCodes codes, HashSet<int> into)
    {
        var listName = hacNumber < 10 ? $"hac0{hacNumber}_proc" : $"hac{hacNumber}_proc";

        foreach (var procedure in codes.Procedures)
        {
            foreach (var operand in procedure.Operands)
            {
                // CMS matches the list name as a substring, not for equality.
                if (operand.ListName.Contains(listName, StringComparison.Ordinal))
                {
                    into.Add(hacNumber);
                    return;
                }
            }
        }
    }

    private static int? Refusal(
        IReadOnlyList<MsDrgHacSecondary> secondaries,
        IReadOnlyList<MsDrgHacSecondary> onAHacList,
        Dictionary<int, List<MsDrgHacSecondary>> met,
        HospitalPoaReporting reporting,
        string? conditionSixOffTheShowList,
        bool anyUnreadablePoa)
    {
        if (met.Count == 0)
        {
            return null;
        }

        if (reporting == HospitalPoaReporting.NonExempt)
        {
            foreach (var (hacNumber, carrying) in met)
            {
                if (hacNumber == CatheterAssociatedUti)
                {
                    if (conditionSixOffTheShowList is not null
                        && carrying.Any(code => IsUnreadable(code.Poa)))
                    {
                        return MissingOnePoa;
                    }

                    continue;
                }

                if (carrying.Any(code => IsUnreadable(code.Poa)) || anyUnreadablePoa)
                {
                    return MissingOnePoa;
                }
            }

            return null;
        }

        if (reporting != HospitalPoaReporting.Unknown)
        {
            return null;
        }

        // CMS walks the HAC-bearing secondaries and returns on the first
        // refusal, so the order of these three is the order of its checks.
        if (SecondariesCountedByCmssTautology(secondaries) >= 2)
        {
            return UnknownStatusMultipleConditions;
        }

        // These two read every secondary that is on a HAC list, not only the
        // ones whose condition was met -- CMS's loop is over the HAC-bearing
        // codes and only the entry into the branch depends on a met condition.
        foreach (var code in onAHacList)
        {
            if (code.Poa is PresentOnAdmission.No or PresentOnAdmission.Unknown)
            {
                return UnknownStatusPoaNotPresent;
            }

            if (IsUnreadable(code.Poa))
            {
                return UnknownStatusPoaUnreadable;
            }
        }

        return null;
    }
}
