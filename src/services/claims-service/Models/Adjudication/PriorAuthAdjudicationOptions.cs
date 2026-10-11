namespace ClaimsService.Models.Adjudication;

/// <summary>
/// <c>ClaimsAdjudication:PriorAuth</c>: which claim lines the prior-authorization
/// check covers (G-25).
/// </summary>
public sealed class PriorAuthAdjudicationOptions
{
    public const string SectionName = "ClaimsAdjudication:PriorAuth";

    /// <summary>
    /// Also check lines whose procedure code the claim's plan flags as
    /// requiring prior authorization (<c>benefits[].priorAuthRequired</c> /
    /// <c>requiresPriorAuth</c> with <c>cptCodes</c>). <b>Off by default</b>
    /// until line-level authorization decisions (G-07, D-06) land:
    /// <c>/validate</c> with a procedure code answers Invalid for any
    /// authorization decided through the API (no line <c>ServiceStatus</c> is
    /// written), so turning this on today would deny (CARC 197) plan-flagged
    /// lines of claims whose authorization was approved. The default scope —
    /// every line of an institutional Medicaid inpatient (POS 21) claim — is
    /// unaffected by this setting.
    ///
    /// <para>
    /// <b>Prerequisites for turning it on</b> (owner decision 2026-10-11,
    /// docs/epa-gap-matrix.md §7.1 Q-15), besides G-07:
    /// <list type="number">
    ///   <item>D-36 — examiner approval re-runs call coverage-service with the
    ///   examiner's token, which lacks <c>coverage:read</c>;</item>
    ///   <item>D-40 — plan lookups (<c>HttpBenefitPlanResolver</c>) forward the
    ///   caller's token: on an approval re-run every examiner role must hold
    ///   <c>benefits:read</c> in the stored TenantRole catalogue, or plan lookups
    ///   must use the service identity (<c>UseChoServiceIdentity</c>). Otherwise
    ///   every approval re-run of a claim outside the floor pends PLANUNAVAIL.</item>
    /// </list>
    /// </para>
    /// </summary>
    public bool UsePlanFlaggedCodes { get; set; }
}
