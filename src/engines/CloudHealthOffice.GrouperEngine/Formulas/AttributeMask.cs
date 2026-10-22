using System.Collections;

namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// The operands a stay satisfies, each tagged with the position it was
/// satisfied at.
///
/// <para>
/// A stay's principal diagnosis, its secondaries and its procedures all
/// contribute operands drawn from the same CMS lists, and the position they
/// came from is part of the entry: the principal diagnosis contributes
/// <c>PDX:</c> entries, each secondary contributes <c>SDX:</c> entries, and
/// procedures contribute unprefixed ones. A formula operand then matches only
/// at the position it asks for, which is why <c>PDX:incident</c>,
/// <c>SDX:incident</c> and <c>ONLY:incident</c> are three different tests over
/// one list.
/// </para>
///
/// <para>
/// <b>Why this is a list and not a hash set.</b> CMS's matching rule has one
/// special case: <c>ANY:</c> on either side matches <c>PDX:</c> or <c>SDX:</c>
/// on the other. That makes the relation non-transitive —
/// <c>ANY:x</c> matches <c>PDX:x</c> and <c>ANY:x</c> matches <c>SDX:x</c>,
/// but <c>PDX:x</c> does not match <c>SDX:x</c>. A relation that is not
/// transitive cannot be expressed as hash-code equality, so a
/// <c>HashSet&lt;OperandName&gt;</c> would silently give wrong answers for
/// exactly the operands the prefix was introduced to distinguish. CMS reaches
/// the same conclusion by construction: its mask is an <c>ArrayList</c> and
/// every lookup is a linear scan. A mask holds a few dozen entries, so the
/// scan is not worth optimising away.
/// </para>
/// </summary>
public sealed class AttributeMask : IAttributeMask, IEnumerable<MaskAttribute>
{
    private readonly List<MaskAttribute> entries = [];

    /// <summary>An empty mask.</summary>
    public AttributeMask()
    {
    }

    /// <summary>A mask holding the given entries, in order.</summary>
    public AttributeMask(IEnumerable<MaskAttribute> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries.AddRange(entries);
    }

    /// <summary>
    /// A mask holding the given operands, none of them suppressed. The
    /// convenient form for a test.
    /// </summary>
    public AttributeMask(IEnumerable<OperandName> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            this.entries.Add(new MaskAttribute(entry));
        }
    }

    /// <summary>How many entries the mask holds, duplicates included.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// Adds an entry. Duplicates are kept, as CMS keeps them: the mask records
    /// what the claim contributed, and two secondary diagnoses on the same list
    /// are two contributions.
    /// </summary>
    public void Add(OperandName attribute) => entries.Add(new MaskAttribute(attribute));

    /// <summary>Adds an entry, with the MDCs it is suppressed in.</summary>
    public void Add(MaskAttribute attribute) => entries.Add(attribute);

    /// <summary>Adds every entry in <paramref name="attributes"/>.</summary>
    public void AddRange(IEnumerable<MaskAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        entries.AddRange(attributes);
    }

    /// <inheritdoc/>
    public bool Contains(OperandName attribute)
    {
        foreach (var entry in entries)
        {
            if (Matches(attribute, entry.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public bool ContainsUnsuppressed(OperandName attribute, int mdc)
    {
        foreach (var entry in entries)
        {
            if (Matches(attribute, entry.Name) && !entry.IsSuppressedIn(mdc))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// CMS's matching rule, with <paramref name="formulaAtom"/> as the operand
    /// a formula asked for and <paramref name="maskEntry"/> as something the
    /// claim contributed.
    ///
    /// <para>
    /// The list name is compared case-sensitively. That is not an oversight:
    /// CMS compares prefix <i>strings</i> case-insensitively when parsing an
    /// operand, but compares list names with <c>Objects.equals</c> when
    /// matching. Both tables and formulas come from the same CMS release, so
    /// the spellings agree; loosening the comparison here would hide a
    /// table-import problem rather than fix one.
    /// </para>
    /// </summary>
    public static bool Matches(OperandName formulaAtom, OperandName maskEntry)
    {
        var sameName = string.Equals(
            formulaAtom.ListName, maskEntry.ListName, StringComparison.Ordinal);

        if (formulaAtom.Position == OperandPosition.AnyDiagnosis)
        {
            return sameName && maskEntry.Position
                is OperandPosition.AnyDiagnosis
                or OperandPosition.PrincipalDiagnosis
                or OperandPosition.SecondaryDiagnosis;
        }

        if (maskEntry.Position == OperandPosition.AnyDiagnosis)
        {
            return sameName && formulaAtom.Position
                is OperandPosition.PrincipalDiagnosis
                or OperandPosition.SecondaryDiagnosis;
        }

        return sameName && formulaAtom.Position == maskEntry.Position;
    }

    /// <inheritdoc/>
    public IEnumerator<MaskAttribute> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// The mask as formula text, for an audit record. Entries appear in the
    /// order the claim contributed them.
    /// </summary>
    public override string ToString() => string.Join(", ", entries);
}
