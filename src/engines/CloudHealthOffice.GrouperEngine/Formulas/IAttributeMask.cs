namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// The set of operands a stay satisfies, against which a formula is evaluated.
///
/// <para>
/// This is deliberately an interface rather than a set type. CMS's mask is a
/// <c>List&lt;Attribute&gt;</c> tested with <c>List.contains</c>, and the
/// equality that <c>contains</c> uses is not an equivalence relation, so no
/// hash-based set can stand in for it. See <see cref="AttributeMask"/>.
/// </para>
/// </summary>
public interface IAttributeMask
{
    /// <summary>
    /// True when the stay satisfies <paramref name="attribute"/> as a formula
    /// would test it — the position prefix included.
    /// </summary>
    bool Contains(OperandName attribute);
}
