namespace ClaimsService.Models.Adjudication;

/// <summary>
/// What <see cref="Services.Adjudication.Stages.ThirdPartyLiabilityStage"/>
/// decided for a Medicaid claim, kept on the context for the claim record,
/// recovery tracking and the examiner.
/// </summary>
public sealed class TplOutcome
{
    public TplDecision Decision { get; init; }

    /// <summary>Stable reason code (<c>tpl-*</c>).</summary>
    public string ReasonCode { get; init; } = string.Empty;

    public string? Message { get; init; }

    /// <summary>The TPL resources that drove the decision.</summary>
    public IReadOnlyList<TplOutcomeResource> Resources { get; init; } = [];

    /// <summary>Lines another payer covers and Medicaid must cost-avoid.</summary>
    public IReadOnlyList<int> CostAvoidLines { get; init; } = [];

    /// <summary>Lines Medicaid pays although TPL covers them (exempt service
    /// or pay-and-chase-only resource): recovery is pursued after payment.</summary>
    public IReadOnlyList<TplPayAndChaseLine> PayAndChaseLines { get; init; } = [];
}

public sealed record TplOutcomeResource(string Id, string State, string CarrierName, string? PolicyNumber, string? StateCoverageCode);

public sealed record TplPayAndChaseLine(int LineNumber, string Reason);

public enum TplDecision
{
    /// <summary>No TPL applies; Medicaid pays.</summary>
    NoTpl,
    /// <summary>Medicaid pays and recovers afterwards.</summary>
    PayAndChase,
    /// <summary>The claim is denied so the provider bills the other carrier.</summary>
    CostAvoided,
    /// <summary>An examiner decides (partial scope, lookup failed, Pend mode).</summary>
    Pended,
    /// <summary>The provider already billed the other carrier (prior-payer
    /// data on the 837); COB handles the balance.</summary>
    OtherPayerBilled,
}
