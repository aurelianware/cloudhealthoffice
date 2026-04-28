namespace BenefitPlanService.Models;

/// <summary>
/// Configuration for the service-category mapping repository, admin write API,
/// and system-default seed loader (capability BP 5.6 — Service Category Mapping).
///
/// <para>
/// The repository is read on every adjudicated claim line, so a short
/// in-process cache wraps the per-(tenant, plan) mapping list. The admin
/// write endpoints sit behind a defence-in-depth config gate — the deployment
/// layer (NetworkPolicy / gateway ACL) is the load-bearing control. The seed
/// loader applies a per-installation curated mapping set on first read for a
/// tenant; re-applies are admin-triggered when the seed version changes.
/// </para>
///
/// <para>
/// See <c>docs/architecture/service-category-mapping.md</c> for the canonical
/// resolution flow and the documented incoherence between
/// <c>Benefit.ServiceCategory</c> (free-text plan-author label) and
/// <c>ServiceTypeCode</c> (resolver output) — addressed by a future
/// translation-layer capability.
/// </para>
/// </summary>
public sealed class ServiceCategoryMappingOptions
{
    public const string SectionName = "ServiceCategoryMapping";

    /// <summary>
    /// Defence-in-depth gate for the admin write endpoints
    /// (POST/PUT/DELETE on <c>/api/v1/service-category-mappings</c>).
    /// Default <c>false</c>: a misconfigured gateway or NetworkPolicy can't
    /// expose the routes just because they're registered. Operators must
    /// explicitly opt in via <c>ServiceCategoryMapping:AdminWriteEnabled=true</c>
    /// AND restrict access at the deployment layer. When disabled, write
    /// endpoints return 503 Service Unavailable; GET endpoints stay open.
    /// </summary>
    public bool AdminWriteEnabled { get; set; } = false;

    /// <summary>
    /// In-process MemoryCache TTL for <c>(tenantId, benefitPlanId)</c>
    /// mapping lookups. Default 5 minutes — short enough that
    /// operator-authored changes propagate within an authoring session,
    /// long enough that a per-line resolver call doesn't repeatedly hit
    /// the underlying store on adjudication-heavy traffic.
    ///
    /// <para>
    /// Tunable per environment: integration tests run with Zero (effectively
    /// no caching) so per-test fixtures can mutate the underlying store
    /// between assertions; local development can drop to <c>30s</c> for
    /// authoring iteration; production keeps the 5-minute default.
    /// </para>
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the <c>SystemDefaultMappingSeeder</c> hosted service runs
    /// at startup. Default <c>true</c>; tests disable to avoid touching
    /// the underlying store.
    ///
    /// <para>
    /// The seeder is idempotent — it tracks which seed version has been
    /// applied per tenant in a <c>SystemDefaultsApplied</c> document and
    /// skips when the recorded version matches the seed file.
    /// </para>
    /// </summary>
    public bool SeedSystemDefaultsOnStartup { get; set; } = true;

    /// <summary>
    /// Filesystem path to the JSON seed file relative to the service
    /// content root. Default <c>schemas/service-category-mappings/system-defaults.json</c>.
    /// Override in tests to point at a fixture.
    /// </summary>
    public string SeedFilePath { get; set; }
        = "schemas/service-category-mappings/system-defaults.json";

    /// <summary>
    /// Maximum number of <c>ProcedureCodeRule</c> entries accepted in a
    /// single create/update request. Defends against accidental oversized
    /// payloads. Default 1,000 — large enough for any realistic operator
    /// authoring session, small enough to keep the request bounded.
    /// </summary>
    public int MaxRulesPerMapping { get; set; } = 1_000;
}
