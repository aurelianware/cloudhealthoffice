namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// How a code-driven operand decides. One per node class in CMS's supplier.
/// </summary>
public enum CodeDrivenRule
{
    /// <summary>
    /// No rule. A special list in the table that nothing implements, which is
    /// refused rather than answered false.
    /// </summary>
    None,

    /// <summary>
    /// Two different lists found among the diagnoses, at most one per code.
    /// CMS's <c>SignificantTrauma</c>.
    /// </summary>
    TwoDistinctDiagnosisLists,

    /// <summary>
    /// Two different lists found among the procedures, at most one per code.
    /// CMS's <c>BilateralNode</c>.
    /// </summary>
    TwoDistinctProcedureLists,

    /// <summary>
    /// Every list present, and three occurrences in total. CMS's
    /// <c>CountThreeNode</c>.
    /// </summary>
    EveryListAndThreeOccurrences,

    /// <summary>
    /// Every list present, and two occurrences in total. CMS's
    /// <c>CountTwoNode</c>.
    /// </summary>
    EveryListAndTwoOccurrences,

    /// <summary>
    /// Two occurrences in total, with no requirement that both lists appear.
    /// CMS's <c>CountFusionsNode</c>.
    /// </summary>
    TwoOccurrences,

    /// <summary>
    /// The occurrences' own weights adding to four. CMS's
    /// <c>CountFourNode</c>, whose <c>vesselN</c> and <c>stentN</c> lists are
    /// worth N each.
    /// </summary>
    WeightedOccurrencesReachingFour,

    /// <summary>
    /// The principal diagnosis is in a given MDC. CMS's
    /// <c>MdcFallThruNode</c>.
    /// </summary>
    PrincipalDiagnosisInMdc,
}

/// <summary>
/// One list a code-driven operand counts, and what an occurrence of it is
/// worth. Everything but the vessel and stent lists is worth one.
/// </summary>
/// <param name="ListName">The list name as the tables spell it.</param>
/// <param name="Weight">What one occurrence contributes to the count.</param>
public readonly record struct CodeDrivenTerm(string ListName, int Weight = 1);

/// <summary>
/// Evaluates the operands that read the claim's codes rather than its mask.
///
/// <para>
/// Eleven V44 operand names work this way, and every one of them appears in a
/// real V440 formula. Each was put to CMS's own grouper rather than inferred
/// from its source, because four of the rules are not what the names suggest:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     <c>MULTST</c> and <c>BILATERAL</c> take <b>at most one list per
///     code</b>. A single diagnosis carrying both <c>sthead</c> and
///     <c>stchest</c> does not satisfy <c>MULTST</c>; two codes carrying one
///     each do.
///   </description></item>
///   <item><description>
///     <c>BILATERAL</c> is satisfied by any two of its six lists, so a right
///     hip and a right knee satisfy it. It does not check that the two are a
///     left/right pair.
///   </description></item>
///   <item><description>
///     <c>MULTFUSE</c> requires two occurrences but <b>not</b> two different
///     lists, where <c>SINGLEFUSIONCTR</c> requires both: the same list twice
///     satisfies the first and not the second.
///   </description></item>
///   <item><description>
///     The vessel and stent counts are <b>weighted</b>. One <c>vessel4</c>
///     satisfies <c>FOUR_VESSELS</c> on its own; three <c>vessel1</c> do not.
///   </description></item>
/// </list>
///
/// <para>
/// And one more naming trap, found the same way: two of CMS's node-factory
/// members are not named after the operand they match. <c>SGLANTFUSECTR</c>
/// matches the operand <c>SGLANTCTR</c> and <c>SGLPOST1FUSECTR</c> matches
/// <c>SGLPOST1CTR</c>. A table keyed on the member names would have left both
/// counters as ordinary mask lookups, which are always false.
/// </para>
/// </summary>
public static class CodeDrivenOperand
{
    /// <summary>
    /// Evaluates <paramref name="list"/> against the claim's codes.
    /// </summary>
    /// <exception cref="DrgFormulaUnsupportedException">
    /// The list has no rule. Refusing is deliberate: each of these operands is
    /// the whole condition of the formula it sits in, so a false answer would
    /// move the stay to another DRG without saying so.
    /// </exception>
    public static bool Evaluate(SpecialLogicList list, ClaimCodes codes)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(codes);

        switch (list.Rule)
        {
            case CodeDrivenRule.TwoDistinctDiagnosisLists:
                return TwoDistinctLists(
                    list.Terms, codes.Diagnoses, (code, name) => code.HasOperand(name));

            case CodeDrivenRule.TwoDistinctProcedureLists:
                return TwoDistinctLists(
                    list.Terms, codes.Procedures, (code, name) => code.HasOperand(name));

            case CodeDrivenRule.EveryListAndThreeOccurrences:
            {
                var (lists, total) = Count(list, codes, weighted: false);
                return lists == list.Terms.Count && total > 2;
            }

            case CodeDrivenRule.EveryListAndTwoOccurrences:
            {
                var (lists, total) = Count(list, codes, weighted: false);
                return lists == list.Terms.Count && total > 1;
            }

            case CodeDrivenRule.TwoOccurrences:
                return Count(list, codes, weighted: false).Total > 1;

            case CodeDrivenRule.WeightedOccurrencesReachingFour:
                return Count(list, codes, weighted: true).Total >= 4;

            case CodeDrivenRule.PrincipalDiagnosisInMdc:
                return codes.Diagnoses.Count > 0
                    && codes.Diagnoses[0].Mdc == list.FallThroughMdc;

            default:
                throw new DrgFormulaUnsupportedException(list.CanonicalName);
        }
    }

    /// <summary>
    /// CMS's <c>SignificantTrauma</c> and <c>BilateralNode</c>, which share a
    /// shape: walk the codes in order and take from each code the first list
    /// it carries that has not already been found. One code can therefore
    /// contribute only one list, however many it carries.
    /// </summary>
    private static bool TwoDistinctLists<TCode>(
        IReadOnlyList<CodeDrivenTerm> terms,
        IReadOnlyList<TCode> codes,
        Func<TCode, string, bool> carries)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var code in codes)
        {
            if (found.Count == 2)
            {
                break;
            }

            foreach (var term in terms)
            {
                if (!found.Contains(term.ListName) && carries(code, term.ListName))
                {
                    found.Add(term.ListName);
                    break;
                }
            }
        }

        return found.Count == 2;
    }

    /// <summary>
    /// The counting nodes' shared body: over the procedures, every list each
    /// code carries contributes, so one code carrying two of them counts
    /// twice.
    /// </summary>
    /// <returns>
    /// How many distinct lists were seen, and the total the occurrences come
    /// to.
    /// </returns>
    private static (int Lists, int Total) Count(
        SpecialLogicList list, ClaimCodes codes, bool weighted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var procedure in codes.Procedures)
        {
            // CMS stops here once the weighted count is in, which changes
            // which codes it marks as having affected the DRG but not the
            // answer.
            if (weighted && total >= 4)
            {
                break;
            }

            foreach (var term in list.Terms)
            {
                if (!procedure.HasOperand(term.ListName))
                {
                    continue;
                }

                seen.Add(term.ListName);
                total += weighted ? term.Weight : 1;
            }
        }

        return (seen.Count, total);
    }
}
