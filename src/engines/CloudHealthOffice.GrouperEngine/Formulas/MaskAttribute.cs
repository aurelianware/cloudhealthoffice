namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// One entry in a stay's mask: an operand the stay satisfies, together with the
/// MDCs in which it does not count.
///
/// <para>
/// The suppression is there for procedure clusters. When a combination of
/// procedure codes satisfies a cluster, CMS adds the cluster's own operands to
/// the mask and marks each contributing procedure with the MDCs the cluster
/// restricts. In those MDCs the individual procedures' operands must not count
/// — otherwise a stay would be credited both for the combination and for its
/// parts, and would group as though it had two qualifying procedures.
/// </para>
///
/// <para>
/// Whether suppression applies is a property of the <i>evaluation</i>, not of
/// the operand: CMS evaluates the same formula text with suppression on for
/// grouping and off for some other paths. See
/// <see cref="DrgFormulaEvaluation"/>.
/// </para>
/// </summary>
/// <param name="Name">The operand, with its position.</param>
/// <param name="SuppressedMdcs">
/// A bit per MDC: bit <c>n</c> set means this entry does not count when
/// grouping in MDC <c>n</c>. Zero — the common case — means it always counts.
/// CMS uses a 32-bit set here and the MDC numbers run to 29, so an
/// <see cref="int"/> holds all of them.
/// </param>
public readonly record struct MaskAttribute(OperandName Name, int SuppressedMdcs = 0)
{
    /// <summary>True when this entry does not count in <paramref name="mdc"/>.</summary>
    public bool IsSuppressedIn(int mdc) =>
        mdc is >= 0 and < 32 && (SuppressedMdcs & (1 << mdc)) != 0;

    /// <summary>Turns a set of MDC numbers into the bit mask this record holds.</summary>
    public static int MdcMask(IEnumerable<int> mdcs)
    {
        ArgumentNullException.ThrowIfNull(mdcs);

        var mask = 0;
        foreach (var mdc in mdcs)
        {
            if (mdc is >= 0 and < 32)
            {
                mask |= 1 << mdc;
            }
        }

        return mask;
    }

    /// <inheritdoc/>
    public override string ToString() => SuppressedMdcs == 0
        ? Name.ToString()
        : $"{Name} (not in MDC {string.Join(",", SuppressedMdcList())})";

    private IEnumerable<int> SuppressedMdcList()
    {
        for (var mdc = 0; mdc < 32; mdc++)
        {
            if ((SuppressedMdcs & (1 << mdc)) != 0)
            {
                yield return mdc;
            }
        }
    }
}
