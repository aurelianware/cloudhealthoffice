namespace ClaimsService.Models.Migrations;

/// <summary>
/// Per-call summary returned to the operator after the migration
/// endpoint completes (capability 5.1b — Cosmos partition-key
/// migration to <c>/tenantId</c>).
///
/// <para>
/// Counters are intentionally explicit rather than derived (e.g.
/// <c>DocumentsRead = DocumentsMigrated + DocumentsSkipped + DocumentsErrored</c>)
/// so a partial run that bailed mid-batch surfaces the discrepancy
/// directly instead of forcing the operator to reconcile it.
/// </para>
/// </summary>
public sealed class ClaimMigrationResult
{
    public string MigrationRunId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Wall-clock start of the run (UTC).</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Wall-clock end of the run (UTC). Set on completion.</summary>
    public DateTime CompletedAt { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>True when this run did not write to the target container.</summary>
    public bool DryRun { get; set; }

    public string SourceContainer { get; set; } = string.Empty;
    public string TargetContainer { get; set; } = string.Empty;

    /// <summary>Documents read from the source container.</summary>
    public int DocumentsRead { get; set; }

    /// <summary>Documents written (or, in dry-run, would-have-been-written) to the target.</summary>
    public int DocumentsWritten { get; set; }

    /// <summary>Documents skipped because the target already contains a row with the same <c>Id</c>.</summary>
    public int DocumentsSkipped { get; set; }

    /// <summary>Documents that failed during write. Each entry is itemized in <see cref="Issues"/>.</summary>
    public int DocumentsErrored { get; set; }

    /// <summary>
    /// Documents that required hydration (legacy <c>ClaimVersionId == ""</c>
    /// rows) before being written. Operator visibility into how much
    /// pre-versioning data the run canonicalized.
    /// </summary>
    public int DocumentsHydrated { get; set; }

    /// <summary>
    /// Outcome label for the overall run. <c>success</c> when
    /// <see cref="DocumentsErrored"/> is zero; <c>partial</c> when at
    /// least one row failed but the run completed; <c>failed</c> when
    /// the run aborted before completion (e.g. cancellation).
    /// </summary>
    public string Outcome { get; set; } = "success";

    public List<ClaimMigrationIssue> Issues { get; set; } = new();
}

/// <summary>
/// One per-document issue surfaced during a migration run.
/// </summary>
public sealed class ClaimMigrationIssue
{
    public string ClaimId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>
/// Status snapshot returned by the
/// <c>GET /api/v1/admin/claims/cosmos-migration/status</c> endpoint.
/// Decision 15 (ratified): GET status endpoint IN; chunked progress
/// streaming OUT. This is the operator-facing observability surface
/// alongside the structured logs and Prometheus counters.
/// </summary>
public sealed class ClaimMigrationStatus
{
    public bool MigrationsEnabled { get; set; }
    public string SourceContainer { get; set; } = string.Empty;
    public string TargetContainer { get; set; } = string.Empty;
    public int BatchSize { get; set; }

    /// <summary>
    /// True while a run is currently executing. Consecutive operator
    /// triggers while this is true return 409 Conflict.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>Last completed run summary (any outcome). Null when no run has completed yet.</summary>
    public ClaimMigrationResult? LastRun { get; set; }
}
