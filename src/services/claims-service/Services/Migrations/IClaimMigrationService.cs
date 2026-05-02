using ClaimsService.Models.Migrations;

namespace ClaimsService.Services.Migrations;

/// <summary>
/// Capability 5.1b — copies claim documents from the legacy
/// <c>Claims</c> Cosmos container (Bicep <c>/memberId</c> declaration,
/// <c>/Id</c> runtime partition) to the canonical <c>ClaimsV2</c>
/// container (<c>/TenantId</c> partition).
///
/// <para>
/// Idempotent: re-running skips rows already present in the target
/// container by <c>Id</c> (Decision 7 — batched idempotency check).
/// Hydrates legacy rows missing versioning fields
/// (<c>ClaimVersionId == ""</c> → <c>Id</c>; <c>VersionNumber == 0</c>
/// → 1; <c>VersionState == Unknown</c> → mapped from <c>Status</c>) so
/// the new container starts fully canonicalized day one.
/// </para>
///
/// <para>
/// Driven by the admin endpoint
/// <c>POST /api/v1/admin/claims/cosmos-migration/run</c>; the service
/// has no HttpContext dependency and is callable from any system
/// actor. The status surface is reported through <see cref="GetStatus"/>
/// (Decision 15 — GET status IN, chunked streaming OUT).
/// </para>
///
/// <para>
/// See <c>docs/migrations/claims-cosmos-partition-migration.md</c>
/// for the operator runbook.
/// </para>
/// </summary>
public interface IClaimMigrationService
{
    /// <summary>
    /// Run the migration end-to-end. Setting <see cref="ClaimMigrationRequest.DryRun"/>
    /// produces counters without writing to the target container.
    /// </summary>
    Task<ClaimMigrationResult> RunAsync(ClaimMigrationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Snapshot of current migration state (running / last-completed
    /// summary / configured source-target pair).
    /// </summary>
    ClaimMigrationStatus GetStatus();
}
