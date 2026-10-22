namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// CMS's own marker on a DRG logic row: which side of an MDC's
/// medical/surgical split the row's DRG sits on.
///
/// <para>
/// Deliberately not <see cref="Domain.DrgPartition"/>, which is the domain's
/// notion of where a <i>stay</i> landed and whose third value is pre-MDC.
/// CMS's third value means something else — that the row does not assign a
/// DRG at all — and mapping one onto the other would say a stay was grouped
/// before MDC assignment when it was not grouped at all.
/// </para>
/// </summary>
public enum DrgTablePartition
{
    /// <summary>
    /// Neither. CMS marks the rows that are not DRG assignments this way —
    /// the MDC-assignment rows and the MCE edits, which share the table.
    /// </summary>
    NotApplicable,

    /// <summary>A medical DRG: no operating-room procedure.</summary>
    Medical,

    /// <summary>A surgical DRG.</summary>
    Surgical,
}

/// <summary>
/// One row of CMS's DRG logic table: a formula, where it sits in its MDC's
/// ranking, and what matching it means.
///
/// <para>
/// <b>Rank is the whole of the branching logic.</b> An MDC's rows are tried in
/// rank order and the first formula that matches wins; there is no scoring and
/// no tie-break among matches. So a row's rank is as load-bearing as its
/// formula, and a ranking read in the wrong order produces a valid-looking DRG
/// on every claim.
/// </para>
///
/// <para>
/// <b>Not every row is a DRG.</b> 56 of V44's 824 rows carry DRG 0 and no
/// partition: the <c>MDCnn</c> rows that decide which MDC a stay belongs to,
/// and the MCE edits. They live in the same table and are ranked alongside the
/// DRG rows, and in 14 MDCs an <c>MDCnn</c> row shares a rank with a real DRG
/// row. That tie is harmless, because no diagnosis or procedure code in V44
/// carries an <c>MDCnn</c> operand, so the row cannot match while a DRG is
/// being assigned — but it is a tie, so equal ranks keep the table's own
/// order rather than being reordered.
/// </para>
/// </summary>
/// <param name="Mdc">The MDC whose ranking this row belongs to.</param>
/// <param name="Rank">
/// Position in that ranking, ascending. Not necessarily unique.
/// </param>
/// <param name="Drg">
/// The DRG a match assigns, or 0 for a row that assigns none.
/// </param>
/// <param name="BaseDrg">
/// The DRG before the severity split, which is what a severity-split family
/// shares.
/// </param>
/// <param name="Partition">Medical, surgical, or neither.</param>
/// <param name="ReRouteMdc">
/// The MDC to group the stay under instead, or 0 for none. 20 V44 rows carry
/// one.
/// </param>
/// <param name="FormulaText">The formula as CMS writes it, for the audit record.</param>
/// <param name="Formula">The parsed formula.</param>
/// <param name="SeveritySuppressionOperands">
/// The lists this row takes out of the severity count. CMS writes one empty
/// string where a row suppresses nothing, which is 768 of the 824.
/// </param>
/// <param name="DrgSeverity">
/// CMS's severity marker on the row itself. Carried through for the audit
/// record; the severity a stay is credited with is computed per row.
/// </param>
public sealed record MsDrgFormulaRow(
    int Mdc,
    int Rank,
    int Drg,
    int BaseDrg,
    DrgTablePartition Partition,
    int ReRouteMdc,
    string FormulaText,
    DrgFormulaExpression Formula,
    IReadOnlyList<string> SeveritySuppressionOperands,
    int DrgSeverity)
{
    /// <summary>
    /// Whether a match on this row assigns a DRG. False for the
    /// MDC-assignment rows and the MCE edits.
    /// </summary>
    public bool AssignsDrg => Drg != 0;
}

/// <summary>
/// CMS's DRG logic table, for one grouper version.
/// </summary>
public interface IMsDrgFormulaSource
{
    /// <summary>The grouper version these rows came from.</summary>
    string Version { get; }

    /// <summary>The MDCs the table holds a ranking for.</summary>
    IReadOnlyCollection<int> Mdcs { get; }

    /// <summary>
    /// The MDC's rows in rank order, ties in the table's own order. Empty for
    /// an MDC the table does not hold.
    /// </summary>
    IReadOnlyList<MsDrgFormulaRow> ForMdc(int mdc);
}
