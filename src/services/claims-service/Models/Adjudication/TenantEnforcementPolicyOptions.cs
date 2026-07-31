namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Service-wide enforcement-posture configuration consumed by
/// <see cref="Services.Adjudication.Stages.NetworkCredentialingStage"/>
/// (capability 5.6) and <see cref="Services.Adjudication.Stages.NcciEditsStage"/>
/// (capability 5.7). Bound from configuration section
/// <c>Adjudication:Enforcement</c>.
///
/// <para>
/// Phase 1 is service-wide — every tenant runs the same enforcement
/// posture. Per-tenant overrides are deferred to Phase 2 multi-tenant
/// config work to keep the rollout surface small. The class name
/// retains <c>Tenant</c> in the prefix so the future per-tenant
/// override binds onto the same shape rather than introducing a
/// parallel options class.
/// </para>
/// </summary>
public class TenantEnforcementPolicyOptions
{
    public const string SectionName = "Adjudication:Enforcement";

    public NetworkEnforcementMode NetworkMode { get; set; } = NetworkEnforcementMode.FailClosed;

    public CredentialingEnforcementMode CredentialingMode { get; set; }
        = CredentialingEnforcementMode.FailClosed;

    /// <summary>
    /// Posture for NCCI / MUE edit failures (capability 5.7). Default
    /// <see cref="NcciEnforcementMode.PendForReview"/> diverges from the
    /// other modes' FailClosed default because NCCI failures often have
    /// a legitimate modifier-override path; auto-denial without review
    /// is operationally harsh.
    /// </summary>
    public NcciEnforcementMode NcciMode { get; set; } = NcciEnforcementMode.PendForReview;

    /// <summary>
    /// Posture for CHO-secondary detection (capability 5.8). Default
    /// <see cref="CobEnforcementMode.PendForSecondary"/> mirrors
    /// <see cref="NcciMode"/>'s pend-by-default semantic — Phase 1 ships
    /// CHO-primary adjudication only and CHO-secondary calculation is
    /// genuinely unimplemented, so pending is the honest outcome rather
    /// than denying or silently passing.
    /// </summary>
    public CobEnforcementMode CobMode { get; set; } = CobEnforcementMode.PendForSecondary;

    /// <summary>
    /// Posture for AI-backed examination (capability 5.9). Default
    /// <see cref="AiEnforcementMode.BestEffort"/> — AI examination is
    /// advisory; its absence shouldn't block claim processing because
    /// NCCI failures already have a human work-queue path independent
    /// of AI. <see cref="AiEnforcementMode.Disabled"/> is the operational
    /// kill switch.
    /// </summary>
    public AiEnforcementMode AiMode { get; set; } = AiEnforcementMode.Disabled;
}
