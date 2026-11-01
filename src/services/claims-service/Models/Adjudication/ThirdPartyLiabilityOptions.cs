namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Medicaid third-party liability in adjudication
/// (<see cref="Services.Adjudication.Stages.ThirdPartyLiabilityStage"/>).
/// Section <c>Adjudication:ThirdPartyLiability</c>.
/// </summary>
public sealed class ThirdPartyLiabilityOptions
{
    public const string SectionName = "Adjudication:ThirdPartyLiability";

    /// <summary>Off until a plan has loaded and checked its state TPL file.</summary>
    public TplEnforcementMode Mode { get; set; } = TplEnforcementMode.Off;

    /// <summary>
    /// Plan codes where Medicaid pays before other insurance, so TPL is never
    /// cost-avoided (Texas STAR Health, TMPPM 8.3.1, unless a court orders
    /// otherwise). Matched against the resolved plan's code and id.
    /// </summary>
    public List<string> MedicaidPrimaryPlanCodes { get; set; } = new();

    /// <summary>
    /// Pay pediatric preventive services without regard to TPL (Social
    /// Security Act 1902(a)(25)(E), as amended by BBA 2018). Leave on unless
    /// the state has found 90-day cost avoidance warranted and says so.
    /// </summary>
    public bool PayPediatricPreventive { get; set; } = true;

    /// <summary>Age below which a member counts as pediatric (EPSDT runs to 21).</summary>
    public int PediatricAgeLimit { get; set; } = 21;
}

public enum TplEnforcementMode
{
    /// <summary>The stage does nothing.</summary>
    Off,
    /// <summary>Evaluates and records the outcome on the claim; never changes it.</summary>
    SoftValidation,
    /// <summary>Claims that would be cost-avoided pend for an examiner instead.</summary>
    Pend,
    /// <summary>Claims whose every line is covered by active TPL are denied
    /// CO-22 / MA04 so the provider bills the other carrier first.</summary>
    CostAvoid,
}
