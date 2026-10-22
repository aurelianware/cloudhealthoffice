namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// What kind of thing a special logic list is.
/// </summary>
public enum SpecialLogicKind
{
    /// <summary>
    /// An MCE edit. It is a mask lookup like any other, but a successful one
    /// also sets the grouper's return code, which is how a claim becomes
    /// ungroupable.
    /// </summary>
    GrouperReturnCode,

    /// <summary>
    /// <c>incident</c>. Always tested at the <c>ONLY:</c> position whatever
    /// prefix the formula wrote.
    /// </summary>
    OnlyIncident,

    /// <summary>
    /// A test over the claim's codes rather than its mask: counting fusions,
    /// vessels or stents, bilateral pairs, significant trauma, MDC
    /// fall-through. The mask cannot answer these, and a mask lookup on the
    /// name returns false however the name is spelled.
    /// </summary>
    CodeDriven,
}

/// <summary>
/// One of CMS's "special logic lists": an operand name whose meaning is not
/// set membership.
///
/// <para>
/// <b>Why this table exists at all.</b> CMS resolves these names
/// case-insensitively and then normalises them to the list's canonical
/// spelling, which is not always the spelling the formula uses. A V44 formula
/// says <c>INVALID_PDX</c>; the attribute the node then looks for is
/// <c>invalid_pdx</c>. A port that compared the formula's spelling against the
/// mask would find nothing, and the claim would group as though it had a valid
/// principal diagnosis — no error, just the wrong DRG. CMS's own grouper was
/// asked each of these questions directly and its answers are the test cases.
/// </para>
///
/// <para>
/// <b>And the prefix is discarded.</b> The lookup is on the list name alone,
/// so <c>PDX:incident</c> and <c>SDX:incident</c> are both evaluated as
/// <c>ONLY:incident</c>. That is checkable and was checked: CMS says
/// <c>PDX:incident</c> is true against a mask holding only
/// <c>ONLY:incident</c>, and false against one holding only
/// <c>PDX:incident</c>. It means the V44 MDC 15 formula
/// <c>(PDX:incident &amp; SDX:mdc15fallthrough) | (PDX:mdc15fallthrough &amp;
/// SDX:incident) | ANY:drg794codes</c> does not test the principal and
/// secondary positions separately, however much it looks as though it does.
/// </para>
/// </summary>
/// <param name="CanonicalName">
/// The list name as the tables spell it, which is what the mask holds.
/// </param>
/// <param name="Kind">What the operand does.</param>
/// <param name="Position">
/// The position the operand is tested at, replacing whatever the formula wrote.
/// </param>
/// <param name="ReturnCode">
/// For an MCE edit, the grouper return code a match sets. Zero for everything
/// else.
/// </param>
public sealed record SpecialLogicList(
    string CanonicalName,
    SpecialLogicKind Kind,
    OperandPosition Position = OperandPosition.Unqualified,
    int ReturnCode = 0)
{
    /// <summary>
    /// CMS's table, with the canonical spellings and return codes read from its
    /// own enumeration rather than from the formulas.
    /// </summary>
    private static readonly SpecialLogicList[] All =
    [
        // The MCE edits. Lowercase canonical names; the formulas write them
        // in upper case.
        new("invalid_dstat", SpecialLogicKind.GrouperReturnCode,
            OperandPosition.Unqualified, 5),
        new("invalid_pdx", SpecialLogicKind.GrouperReturnCode,
            OperandPosition.PrincipalDiagnosis, 7),
        new("pdx_ecode", SpecialLogicKind.GrouperReturnCode,
            OperandPosition.PrincipalDiagnosis, 1),
        new("invalid_age", SpecialLogicKind.GrouperReturnCode,
            OperandPosition.Unqualified, 3),
        new("invalid_sex", SpecialLogicKind.GrouperReturnCode,
            OperandPosition.Unqualified, 4),

        new("incident", SpecialLogicKind.OnlyIncident, OperandPosition.Only),

        // Tests over the claim's codes. The names here are the values CMS's
        // enumeration carries, which for two of the counting lists differ from
        // the enum member's own name: SGLANTFUSECTR is spelled SGLANTCTR in the
        // data, and SGLPOST1FUSECTR is SGLPOST1CTR.
        new("MULTST", SpecialLogicKind.CodeDriven),
        new("BILATERAL", SpecialLogicKind.CodeDriven),
        new("FOUR_VESSELS", SpecialLogicKind.CodeDriven),
        new("FOUR_STENTS", SpecialLogicKind.CodeDriven),
        new("SINGLEFUSIONCTR", SpecialLogicKind.CodeDriven),
        new("SGLANTSECTXCTR", SpecialLogicKind.CodeDriven),
        new("SGLANTCTR", SpecialLogicKind.CodeDriven),
        new("SGLPOSTFUSECTR", SpecialLogicKind.CodeDriven),
        new("SGLPOST1CTR", SpecialLogicKind.CodeDriven),
        new("MULTFUSE", SpecialLogicKind.CodeDriven),
        new("MDC_20_FALL_THRU", SpecialLogicKind.CodeDriven),
    ];

    private static readonly Dictionary<string, SpecialLogicList> ByName =
        All.ToDictionary(list => list.CanonicalName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks up a formula operand's list name. Case-insensitive, as CMS's is,
    /// and the prefix the formula wrote is not consulted.
    /// </summary>
    public static bool TryResolve(string listName, out SpecialLogicList list)
    {
        ArgumentNullException.ThrowIfNull(listName);
        return ByName.TryGetValue(listName, out list!);
    }

    /// <summary>
    /// The attribute this operand looks for: the canonical name at the
    /// prescribed position.
    /// </summary>
    public OperandName Attribute => new(Position, CanonicalName);

    /// <summary>Every special list, for tests and for documentation.</summary>
    public static IReadOnlyList<SpecialLogicList> Known => All;
}
