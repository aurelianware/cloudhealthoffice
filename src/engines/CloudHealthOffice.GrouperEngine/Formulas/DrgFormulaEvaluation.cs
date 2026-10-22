namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// The context a formula is evaluated in: the stay's mask, the MDC being
/// tried, and whether cluster suppression applies.
///
/// <para>
/// <b>Suppression is a mode, not a property of an operand.</b> CMS builds two
/// evaluators over the same formula text — one whose plain operands respect
/// cluster MDC suppression and one whose do not — and picks between them by
/// which path is grouping. That is why this is a parameter of the evaluation
/// rather than a flag on the mask or on the operand, and getting it the other
/// way round would have been a wrong DRG on any stay with a procedure cluster.
/// </para>
/// </summary>
/// <param name="Mask">The operands this stay satisfies.</param>
/// <param name="Mdc">
/// The MDC being evaluated. Suppression is relative to it; outside 0..31 no
/// entry is suppressed.
/// </param>
/// <param name="ApplySuppression">
/// Whether a mask entry suppressed in <paramref name="Mdc"/> counts. True is
/// the grouping path.
/// </param>
/// <param name="ReturnCodes">
/// Where an MCE edit operand records the grouper return code it raised. May be
/// <see langword="null"/> when the caller only wants the boolean — but a
/// grouping caller wants it, because that code is how a claim becomes
/// ungroupable and the boolean does not carry it.
/// </param>
public readonly record struct DrgFormulaEvaluation(
    IAttributeMask Mask,
    int Mdc = -1,
    bool ApplySuppression = false,
    GrouperReturnCodeSink? ReturnCodes = null)
{
    /// <summary>True when the stay satisfies <paramref name="attribute"/>.</summary>
    public bool Satisfies(OperandName attribute) => ApplySuppression
        ? Mask.ContainsUnsuppressed(attribute, Mdc)
        : Mask.Contains(attribute);
}
