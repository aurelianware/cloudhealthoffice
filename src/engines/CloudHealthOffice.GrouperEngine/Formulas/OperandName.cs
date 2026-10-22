namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// Where on the claim an operand's codes have to appear for it to be satisfied.
/// </summary>
public enum OperandPosition
{
    /// <summary>
    /// No prefix. The default, and what most operands carry: the position is
    /// implied by the list itself (a procedure list is matched against
    /// procedures, a diagnosis list against diagnoses).
    /// </summary>
    Unqualified,

    /// <summary>
    /// <c>ANY:</c> — satisfied by the principal diagnosis or by any secondary.
    /// </summary>
    AnyDiagnosis,

    /// <summary>
    /// <c>PDX:</c> — satisfied only by the principal diagnosis. By far the most
    /// common prefix: 170 of the 452 distinct operands in V44.
    /// </summary>
    PrincipalDiagnosis,

    /// <summary>
    /// <c>SDX:</c> — satisfied only by a secondary diagnosis.
    /// </summary>
    SecondaryDiagnosis,

    /// <summary>
    /// <c>ONLY:</c> — satisfied only when every diagnosis on the claim is in
    /// the list, rather than at least one.
    /// </summary>
    Only,

    /// <summary>
    /// <c>PT:</c> — a patient attribute (age, sex, discharge status) rather than
    /// a coded diagnosis or procedure.
    /// </summary>
    PatientAttribute,
}

/// <summary>
/// An operand as it appears in a formula: an optional position prefix and a
/// list name.
///
/// <para>
/// CMS writes these as one token — <c>PDX:incident</c>, <c>ANY:renalfail</c>,
/// <c>CC</c> — and its lexer does not treat <c>:</c> as punctuation, so the
/// whole thing is a single atom. The split belongs here, when the operand is
/// resolved against the claim, not in the parser. That matters: the same list
/// appears under three different prefixes in V44 (<c>ONLY:incident</c>,
/// <c>PDX:incident</c>, <c>SDX:incident</c>), and a grouper that ignored the
/// prefix would treat all three as the same test.
/// </para>
/// </summary>
/// <param name="Position">Where the codes must appear.</param>
/// <param name="ListName">The list name, with any prefix removed.</param>
public readonly record struct OperandName(OperandPosition Position, string ListName)
{
    /// <summary>
    /// Splits a formula operand into its position prefix and list name.
    ///
    /// <para>
    /// An unrecognised prefix is <i>not</i> an error and is not stripped: CMS
    /// falls back to no prefix when a prefix string does not match, so a list
    /// name that happens to contain a colon keeps it. Matching is
    /// case-insensitive, as CMS's <c>getPrefixFromString</c> is.
    /// </para>
    /// </summary>
    public static OperandName Parse(string operand)
    {
        ArgumentNullException.ThrowIfNull(operand);

        var colon = operand.IndexOf(':');
        if (colon <= 0)
        {
            return new OperandName(OperandPosition.Unqualified, operand);
        }

        var prefix = operand.AsSpan(0, colon);
        var position = prefix switch
        {
            _ when prefix.Equals("ANY", StringComparison.OrdinalIgnoreCase)
                => OperandPosition.AnyDiagnosis,
            _ when prefix.Equals("PDX", StringComparison.OrdinalIgnoreCase)
                => OperandPosition.PrincipalDiagnosis,
            _ when prefix.Equals("SDX", StringComparison.OrdinalIgnoreCase)
                => OperandPosition.SecondaryDiagnosis,
            _ when prefix.Equals("ONLY", StringComparison.OrdinalIgnoreCase)
                => OperandPosition.Only,
            _ when prefix.Equals("PT", StringComparison.OrdinalIgnoreCase)
                => OperandPosition.PatientAttribute,
            _ => OperandPosition.Unqualified,
        };

        return position == OperandPosition.Unqualified
            ? new OperandName(OperandPosition.Unqualified, operand)
            : new OperandName(position, operand[(colon + 1)..]);
    }

    /// <summary>Renders the operand back to the form CMS writes it in.</summary>
    public override string ToString() => Position switch
    {
        OperandPosition.AnyDiagnosis => $"ANY:{ListName}",
        OperandPosition.PrincipalDiagnosis => $"PDX:{ListName}",
        OperandPosition.SecondaryDiagnosis => $"SDX:{ListName}",
        OperandPosition.Only => $"ONLY:{ListName}",
        OperandPosition.PatientAttribute => $"PT:{ListName}",
        _ => ListName,
    };
}
