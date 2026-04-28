namespace BenefitPlanService.Models;

/// <summary>
/// Configuration for <c>HttpProviderIntegrityGate</c>'s cached-or-live read
/// path (capability 5.10 — verification integrity score surface).
///
/// <para>
/// The gate reads the canonical projection on <c>Provider.IntegrityScore</c>
/// from <c>provider-service</c> by default and only falls back to the live
/// <c>provider-verification-service</c> path when the cached score is null
/// (never refreshed) or staler than
/// <see cref="StalenessFallbackThreshold"/>. See
/// <c>docs/architecture/integrity-score-consumption.md</c> for the
/// canonical decision tree.
/// </para>
///
/// <para>
/// <b>Convention note.</b> <c>benefit-plan-service</c> historically read
/// configuration via direct <c>IConfiguration["..."]</c> indexing.
/// Capability 5.10 introduces the platform-canonical <c>IOptions&lt;&gt;</c>
/// pattern (already used in <c>provider-service</c>'s
/// <c>IntegrityProjectionOptions</c> and <c>NetworkParticipationBackfillOptions</c>)
/// for new configuration. Existing direct-indexed sites are NOT migrated
/// as part of this PR; they migrate incrementally as the surrounding code
/// is touched.
/// </para>
/// </summary>
public sealed class ProviderIntegrityGateOptions
{
    public const string SectionName = "ProviderIntegrityGate";

    /// <summary>
    /// Threshold above which a cached integrity projection is considered
    /// stale and the gate falls back to the live HTTP path. Default
    /// <c>7 days</c> — roughly one missed worker sweep cycle (NPPES has a
    /// 24h window) plus margin.
    ///
    /// <para>
    /// Operators tune per environment: test = 1 hour for fast iteration;
    /// production = 7 days; high-trust environments = 30 days. Configured
    /// independently of <c>IntegrityProjection:StalenessAlertThreshold</c>
    /// in <c>provider-service</c> so operators can alert sooner than
    /// fall-back kicks in (e.g., warn at 5d, fall back at 7d) without a
    /// code change. Defaults match.
    /// </para>
    /// </summary>
    public TimeSpan StalenessFallbackThreshold { get; set; } = TimeSpan.FromDays(7);
}
