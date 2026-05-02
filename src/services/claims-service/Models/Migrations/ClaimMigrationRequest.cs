namespace ClaimsService.Models.Migrations;

/// <summary>
/// Operator-supplied request body for the
/// <c>POST /api/v1/admin/claims/cosmos-migration/run</c> endpoint
/// (capability 5.1b — Cosmos partition-key migration to <c>/tenantId</c>).
///
/// <para>
/// The endpoint is idempotent: re-running with the same source/target
/// pair is safe. Documents already present in the target container by
/// <c>Id</c> are skipped (counted as <c>Skipped</c> in the result).
/// </para>
/// </summary>
public sealed class ClaimMigrationRequest
{
    /// <summary>
    /// When <c>true</c>, the migration runs end-to-end but never writes
    /// to the target container. Counters reflect what *would* have been
    /// migrated. Use this as the first step of any cutover to confirm
    /// row counts and surface any hydration anomalies before the apply
    /// pass.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Optional override for the per-page batch size used to read from
    /// the source container and to query the target container for
    /// already-migrated IDs (Decision 7 — batched idempotency check
    /// rather than per-document RU spend). When unset the option's
    /// configured default is used.
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// Optional actor id for the audit log line emitted when the run
    /// starts and completes. Resolved at the controller boundary from
    /// the request principal when not supplied; defaults to a synthetic
    /// label when no principal is available.
    /// </summary>
    public string? ActorId { get; set; }

    /// <summary>
    /// Optional correlation id surfaced in log lines for cross-service
    /// audit traceability. Not persisted.
    /// </summary>
    public string? CorrelationId { get; set; }
}
