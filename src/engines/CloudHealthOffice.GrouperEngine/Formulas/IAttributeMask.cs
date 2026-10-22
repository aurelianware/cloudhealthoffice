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
    /// would test it — the position prefix included, cluster suppression
    /// ignored.
    /// </summary>
    bool Contains(OperandName attribute);

    /// <summary>
    /// True when the stay satisfies <paramref name="attribute"/> through an
    /// entry that still counts in <paramref name="mdc"/>.
    ///
    /// <para>
    /// The distinction is not "does the operand match and is it suppressed".
    /// It is "is there a matching entry that is not suppressed" — a stay can
    /// satisfy the same operand twice, once through a procedure a cluster has
    /// suppressed and once through one it has not, and then the operand counts.
    /// </para>
    /// </summary>
    bool ContainsUnsuppressed(OperandName attribute, int mdc);
}
