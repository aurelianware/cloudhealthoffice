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
    /// </summary>
    public bool UsePlanFlaggedCodes { get; set; }
}
