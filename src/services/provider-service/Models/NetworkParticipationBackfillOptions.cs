namespace ProviderService.Models;

/// <summary>
/// Configuration for the one-time network-participation panel-gating
/// backfill (capability 5.5).
///
/// <para>
/// Mirrors the configuration shape established by
/// <see cref="IntegrityProjectionOptions"/> in 5.4.5: a default-false
/// admin gate, an iteration page size, and a per-tenant cap. There is
/// no sweep interval because the backfill is operator-triggered, not a
/// recurring background sweep.
/// </para>
///
/// <para>
/// See <c>docs/architecture/network-participation-backfill.md</c> for
/// operational guidance — the deployment-layer ACL is the load-bearing
/// authorization; this flag is a tripwire.
/// </para>
/// </summary>
public sealed class NetworkParticipationBackfillOptions
{
    public const string SectionName = "NetworkParticipationBackfill";

    /// <summary>
    /// Defence-in-depth gate for the admin backfill endpoint
    /// (<c>POST /api/v1/admin/providers/backfill-network-participations</c>).
    /// Default <c>false</c>: a misconfigured gateway / NetworkPolicy
    /// can't expose the endpoint just because the route is registered.
    /// Operators must explicitly opt in by setting
    /// <c>NetworkParticipationBackfill:AdminBackfillEnabled=true</c> in
    /// configuration AND restrict access at the deployment layer
    /// (NetworkPolicy, gateway ACL). When disabled, the controller
    /// returns 503 Service Unavailable.
    /// </summary>
    public bool AdminBackfillEnabled { get; set; } = false;

    /// <summary>
    /// Page size for the per-tenant provider iterator. Default 100;
    /// the backfill reads one page at a time, patches eligible
    /// participations on each provider, and advances. Smaller pages
    /// trade throughput for predictable working-set memory.
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Hard cap on providers inspected per backfill call. Defends
    /// against runaway iteration in pathological tenants and lets
    /// operators preview a backfill in dry-run sized batches before
    /// running an unbounded operation. Null = unbounded
    /// (whole-tenant). Default 10,000.
    /// </summary>
    public int? MaxProvidersPerCall { get; set; } = 10_000;

    /// <summary>
    /// Log-level for the soft-validation warning emitted when a
    /// caller writes a <see cref="NetworkParticipation"/> with all
    /// five panel-gating fields at their type defaults. Default
    /// <c>Warning</c>; ops can downgrade to <c>Information</c> when
    /// telemetry volume is the priority over signal strength, or
    /// upgrade to <c>Error</c> in an environment that tracks the
    /// follow-up hard-validation cutover.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel SoftValidationLogLevel { get; set; }
        = Microsoft.Extensions.Logging.LogLevel.Warning;
}
