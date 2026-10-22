namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// Collects the grouper return codes an evaluation raised.
///
/// <para>
/// An MCE edit operand does two things when it matches: it answers true, and
/// it sets the grouper's return code. The second is what makes the claim
/// ungroupable, and it is not recoverable from the boolean — a formula can
/// match for an edit reason or for a clinical one and the DRG is the same
/// either way. So the codes are collected here, in the order they were raised.
/// CMS keeps a single value and lets a later edit overwrite an earlier one;
/// keeping all of them costs nothing and says more in an audit record, with
/// <see cref="Last"/> giving CMS's answer.
/// </para>
/// </summary>
public sealed class GrouperReturnCodeSink
{
    private readonly List<int> raised = [];

    /// <summary>The codes raised, in order.</summary>
    public IReadOnlyList<int> Raised => raised;

    /// <summary>
    /// The code CMS would report: the last one raised, or null if none were.
    /// </summary>
    public int? Last => raised.Count == 0 ? null : raised[^1];

    /// <summary>Records a code.</summary>
    public void Raise(int returnCode) => raised.Add(returnCode);
}
