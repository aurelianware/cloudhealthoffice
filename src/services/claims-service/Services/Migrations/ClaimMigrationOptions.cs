namespace ClaimsService.Services.Migrations;

/// <summary>
/// Configuration for the capability 5.1b Cosmos partition-key migration
/// (legacy <c>Claims</c> container with <c>/memberId</c> Bicep
/// declaration / <c>/Id</c> runtime partition → canonical
/// <c>ClaimsV2</c> container with <c>/tenantId</c> partition).
///
/// <para>
/// Mirrors the configuration shape established by
/// <c>NetworkTierBackfillOptions</c> in benefit-plan-service: a
/// default-false admin gate plus per-call sizing knobs. The deployment
/// layer (NetworkPolicy / gateway ACL) is the load-bearing
/// authorization — this flag is a defence-in-depth tripwire.
/// </para>
/// </summary>
public sealed class ClaimMigrationOptions
{
    public const string SectionName = "ClaimsCosmosMigration";

    /// <summary>
    /// Defence-in-depth gate for the admin migration endpoint
    /// (<c>POST /api/v1/admin/claims/cosmos-migration/run</c>).
    /// Default <c>false</c>: a misconfigured gateway / NetworkPolicy
    /// can't expose the endpoint just because the route is registered.
    /// Operators must explicitly opt in by setting
    /// <c>ClaimsCosmosMigration:MigrationsEnabled=true</c> in
    /// configuration AND restrict access at the deployment layer
    /// (NetworkPolicy, gateway ACL). When disabled, the controller
    /// returns 503 Service Unavailable.
    /// </summary>
    public bool MigrationsEnabled { get; set; } = false;

    /// <summary>
    /// Source container name (legacy <c>/memberId</c> partition).
    /// Default <c>"Claims"</c>.
    /// </summary>
    public string SourceContainerName { get; set; } = "Claims";

    /// <summary>
    /// Target container name (canonical <c>/tenantId</c> partition,
    /// declared in Bicep at <c>cosmos-db.bicep</c>). Default
    /// <c>"ClaimsV2"</c> per Decision 4 (Cosmos containers cannot be
    /// renamed; version-suffix convention).
    /// </summary>
    public string TargetContainerName { get; set; } = "ClaimsV2";

    /// <summary>
    /// Documents per page when reading from the source container and
    /// when querying the target container for already-migrated IDs
    /// (Decision 7 — batched idempotency check rather than per-doc
    /// reads). Default 100. Operators can override per-call via the
    /// request body.
    /// </summary>
    public int BatchSize { get; set; } = 100;
}
