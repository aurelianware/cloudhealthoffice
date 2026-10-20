namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// What a plan's edit service returns for one claim (contract version 1).
/// No edits means the plan's rules found nothing.
/// </summary>
public sealed record PlanEditResponse
{
    /// <summary>Must be <see cref="PlanEditContract.Version"/>.</summary>
    public string? ContractVersion { get; init; }

    public IReadOnlyList<PlanEdit>? Edits { get; init; }
}

/// <summary>
/// One of the plan's edits that fired. CHO pends the claim with it for an
/// examiner, who upholds or overrides it like any other edit.
/// <list type="bullet">
///   <item><description><b>A review edit</b> has no <see cref="SuggestedCarc"/>:
///     upholding it records that a person reviewed it and changes no payment.</description></item>
///   <item><description><b>A denial edit</b> has a <see cref="SuggestedCarc"/>
///     and names its <see cref="LineNumbers"/>: upholding it denies those lines,
///     group CO with that CARC (and <see cref="SuggestedRarc"/>).</description></item>
/// </list>
/// </summary>
public sealed record PlanEdit
{
    /// <summary>
    /// The plan's identifier for the rule, stable across releases of its
    /// service: letters, digits, '.', '_' and '-', starting with a letter or
    /// digit, at most <see cref="PlanEditContract.MaxRuleIdLength"/> characters.
    /// An examiner's decision is matched to the edit by rule and lines.
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>What the examiner reads, at most <see cref="PlanEditContract.MaxMessageLength"/> characters.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// The claim lines the edit is about. Empty for an edit about the whole
    /// claim, which can only be a review edit.
    /// </summary>
    public IReadOnlyList<int>? LineNumbers { get; init; }

    /// <summary>The CARC an upheld denial edit applies (for example 96 or 204).</summary>
    public string? SuggestedCarc { get; init; }

    /// <summary>An optional RARC to go with <see cref="SuggestedCarc"/> (for example N130).</summary>
    public string? SuggestedRarc { get; init; }
}
