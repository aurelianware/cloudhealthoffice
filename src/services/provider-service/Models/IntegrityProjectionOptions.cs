namespace ProviderService.Models;

/// <summary>
/// Configuration for the integrity-projection write-back path (capability 5.4.5).
///
/// <para>
/// The hosted worker (<c>IntegrityProjectionWorker</c>) sweeps providers that
/// are due for re-verification, calls <c>provider-verification-service</c>
/// over HTTP, and persists the resulting score back onto the head Active
/// version via <c>IProviderRepository.UpdateIntegrityProjectionAsync</c>.
/// </para>
///
/// <para>
/// The composite refresh runs at the *shortest* due window across active
/// sources (NPPES, 24h). Per-source materialized refresh state is a
/// follow-up under capability 5.10 — see
/// <c>docs/architecture/verification-writeback.md</c> "Composite cadence
/// trade-off".
/// </para>
/// </summary>
public sealed class IntegrityProjectionOptions
{
    public const string SectionName = "IntegrityProjection";

    /// <summary>
    /// Master switch. When false, the hosted worker stays idle (admin
    /// backfill + on-demand refresh remain available).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the worker wakes up and looks for due providers. The
    /// loop is gated by per-provider <c>NextVerificationDue</c>, so a
    /// shorter sweep interval doesn't translate into more work — only
    /// faster responsiveness when a provider falls due between sweeps.
    /// Default 1 hour.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Page size for both the per-tenant provider iterator and the
    /// per-batch verification call. The verification service caps batch
    /// size at 100 NPIs (see <c>provider-verification-service/Program.cs</c>);
    /// keep these aligned to avoid extra HTTP round-trips per page.
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Hard cap on providers refreshed per tenant per sweep. Prevents a
    /// fully-due tenant (post-deploy / backfill backlog) from
    /// monopolising worker time at the expense of other tenants.
    /// </summary>
    public int MaxProvidersPerTenantPerSweep { get; set; } = 1000;

    /// <summary>
    /// Refresh-window per source. Composite refresh runs at the
    /// shortest active window — the worker treats <c>NextVerificationDue</c>
    /// as the materialised composite due-date and recomputes it after
    /// each successful refresh as <c>LastVerifiedAt + ShortestActiveWindow()</c>.
    /// </summary>
    public RefreshWindowsOptions Windows { get; set; } = new();

    /// <summary>
    /// Returns the shortest configured refresh window across the active
    /// sources. NPPES at 24h dominates today; if NPPES is disabled,
    /// LEIE/SAM at 24h takes over.
    /// </summary>
    public TimeSpan ShortestActiveWindow()
    {
        var candidates = new[]
        {
            Windows.Nppes,
            Windows.LeieSam,
            Windows.Pecos,
            Windows.OpenPayments,
            Windows.MedicareUtilization,
            Windows.Fsmb,
        };
        var min = TimeSpan.MaxValue;
        foreach (var c in candidates)
        {
            if (c > TimeSpan.Zero && c < min) min = c;
        }
        return min == TimeSpan.MaxValue ? TimeSpan.FromHours(24) : min;
    }
}

/// <summary>
/// Per-source refresh windows. Aligns with regulatory cadences; documented
/// trade-off in <c>verification-writeback.md</c>.
/// </summary>
public sealed class RefreshWindowsOptions
{
    public TimeSpan Nppes { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan LeieSam { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan Pecos { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan OpenPayments { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan MedicareUtilization { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan Fsmb { get; set; } = TimeSpan.FromDays(30);
}
