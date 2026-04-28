namespace BenefitPlanService.Models;

/// <summary>
/// Configuration for the one-time benefit-plan network-tier
/// <c>NetworkId</c> backfill (capability 5.5 — NetworkTier as Reference
/// to Organization).
///
/// <para>
/// Mirrors the configuration shape established by
/// <c>NetworkParticipationBackfillOptions</c> in provider-service 5.5: a
/// default-false admin gate plus per-call sizing knobs. The operation is
/// admin-triggered, per-tenant, and idempotent (only writes a
/// <c>NetworkId</c> on tiers where it is currently null).
/// </para>
///
/// <para>
/// See <c>docs/architecture/network-tier-organization-reference.md</c>
/// for operational guidance. The deployment-layer ACL is the
/// load-bearing authorization — this flag is a tripwire.
/// </para>
/// </summary>
public sealed class NetworkTierBackfillOptions
{
    public const string SectionName = "NetworkTierBackfill";

    /// <summary>
    /// Defence-in-depth gate for the admin backfill endpoint
    /// (<c>POST /api/v1/admin/benefit-plans/backfill-network-tiers</c>).
    /// Default <c>false</c>: a misconfigured gateway / NetworkPolicy can't
    /// expose the endpoint just because the route is registered. Operators
    /// must explicitly opt in by setting
    /// <c>NetworkTierBackfill:AdminBackfillEnabled=true</c> in configuration
    /// AND restrict access at the deployment layer (NetworkPolicy, gateway
    /// ACL). When disabled, the controller returns 503 Service Unavailable.
    /// </summary>
    public bool AdminBackfillEnabled { get; set; } = false;

    /// <summary>
    /// Maximum number of <c>(planId, tierName) → networkId</c> entries
    /// accepted in a single request body. Defends against accidental
    /// oversized payloads. Default 5,000 — large enough for any realistic
    /// per-tenant cutover, small enough to keep the request bounded.
    /// </summary>
    public int MaxMappingsPerCall { get; set; } = 5_000;

    /// <summary>
    /// Log-level for the soft-validation warning emitted when a write
    /// surface produces a <see cref="NetworkTier"/> with no
    /// <see cref="NetworkTier.NetworkId"/>. Default <c>Warning</c>; ops
    /// can downgrade to <c>Information</c> when volume is the priority,
    /// or upgrade in an environment driving the hard-validation cutover.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel SoftValidationLogLevel { get; set; }
        = Microsoft.Extensions.Logging.LogLevel.Warning;
}
