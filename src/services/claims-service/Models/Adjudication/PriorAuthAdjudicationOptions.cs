namespace ClaimsService.Models.Adjudication;

/// <summary>
/// <c>ClaimsAdjudication:PriorAuth</c>: which claim lines the prior-authorization
/// check covers (G-25).
/// </summary>
public sealed class PriorAuthAdjudicationOptions
{
    public const string SectionName = "ClaimsAdjudication:PriorAuth";

    /// <summary>
    /// Also check lines whose plan flags require prior authorization
    /// (<c>benefits[].priorAuthRequired</c> / <c>requiresPriorAuth</c> with CPT codes).
    /// Off by default for staged rollout (G-64). CHO line-level decisions
    /// (G-07) and the service-owned coverage/plan lookups (D-36/D-40) are now
    /// implemented; their completion does not change this setting.
    ///
    /// <para>
    /// Before enabling in a deployment, verify its claims-service token source,
    /// trusted issuer and downstream access, and exercise the intended tenant's
    /// plan flags and claims-to-authorization service-line mapping. The existing
    /// institutional Medicaid inpatient (POS 21) floor is unaffected.
    /// See docs/operations/CLAIMS-ADJUDICATION-SERVICE-IDENTITY.md.
    /// </para>
    /// </summary>
    public bool UsePlanFlaggedCodes { get; set; }

    /// <summary>
    /// Also check lines the plan's PA rule set puts in scope: the rule set its
    /// program resolves to in the prior-auth rule store, which CRD, PAS and the
    /// CMS-0057-F required-services list read (benefit-plan-service
    /// <c>GET api/v1/prior-auth-rules/plans/{planId}/required-services</c>).
    /// Off by default for staged rollout, like <see cref="UsePlanFlaggedCodes"/>.
    ///
    /// <para>
    /// Adds to the other sources, never removes from them. <c>Always</c> and
    /// <c>WithDiagnosis</c> rules apply; quantity limits do not yet. When the
    /// rule set has exemptions (gold card, provider type, member age), which
    /// claims cannot evaluate, a line in scope only through the rule set that
    /// has no valid authorization pends <c>NOAUTH</c> for review instead of
    /// denying. A failed rule set lookup pends <c>PAUNAVAIL</c>.
    /// </para>
    /// </summary>
    public bool UseRuleSet { get; set; }
}
