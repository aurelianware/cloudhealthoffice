using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Abstractions;

// ─────────────────────────────────────────────────────────────────
// State enrollment source — one implementation per state system
// (TX PEMS, CA PAVE, FL FMMIS, NY eMedNY, etc.)
//
// Each source handles its own HTTP/SFTP client, retry policy,
// response normalization, and cache miss behavior.
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Implemented by every state Medicaid enrollment system adapter.
/// Sources are discovered and aggregated by MultiStateEnrollmentAggregator.
/// </summary>
public interface IStateEnrollmentSource
{
    /// <summary>Two-letter state code this source serves (e.g., "TX", "CA", "FL", "NY").</summary>
    string StateCode { get; }

    /// <summary>Human-readable name of the state enrollment system (e.g., "PEMS", "PAVE").</summary>
    string SourceSystemName { get; }

    /// <summary>Lines of business this source can verify enrollment for.</summary>
    LineOfBusiness SupportedLobs { get; }

    /// <summary>
    /// Real-time enrollment lookup for a single NPI as of a given date.
    /// Implementations should check the cache first, then call the live API on a miss.
    /// Returns null when the NPI is completely unknown to the source system.
    /// </summary>
    Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi,
        DateOnly asOfDate,
        CancellationToken ct = default);

    /// <summary>
    /// Batch lookup for a panel of NPIs — used for panel reconciliation.
    /// Default implementation fans out to GetEnrollmentAsync; override for bulk API support.
    /// </summary>
    Task<IReadOnlyList<StateEnrollmentRecord>> GetPanelAsync(
        IEnumerable<string> npis,
        DateOnly asOfDate,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve the current application status for an in-flight enrollment.
    /// Returns null when the applicationId is not found in the source system.
    /// </summary>
    Task<EnrollmentApplication?> GetApplicationStatusAsync(
        string applicationId,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk sync from the state's batch export (SFTP flat file or bulk API).
    /// Designed to run as a KEDA CronJob — typically nightly.
    /// </summary>
    Task<BatchSyncResult> BulkSyncAsync(CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// Decision gate — plugs into PriorAuthDecisionEngine
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Evaluated by the PriorAuthDecisionEngine before rule sets are assessed.
/// A failed gate short-circuits the decision with a denial.
/// </summary>
public interface IEnrollmentDecisionGate
{
    /// <summary>
    /// Evaluate enrollment eligibility for the rendering provider on the requested service date.
    /// </summary>
    /// <param name="npi">Rendering provider NPI.</param>
    /// <param name="taxonomy">Provider taxonomy code for the requested service.</param>
    /// <param name="stateCode">State in which services will be rendered.</param>
    /// <param name="serviceDate">Date services are requested.</param>
    /// <param name="lob">Line of business under which the PA is being requested.</param>
    Task<GateResult> EvaluateAsync(
        string npi,
        string taxonomy,
        string stateCode,
        DateOnly serviceDate,
        LineOfBusiness lob,
        CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// Notification contract — raised by RevalidationAlertEngine
// ─────────────────────────────────────────────────────────────────

public record RevalidationDueEvent
{
    public required string Npi                      { get; init; }
    public required string StateCode                { get; init; }
    public required string SourceSystem             { get; init; }
    public required DateOnly RevalidationDueDate    { get; init; }
    public required int DaysRemaining               { get; init; }
    public decimal? EstimatedRevenueAtRisk          { get; init; }
    public DateTime RaisedAt                        { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Implemented by the host service to receive revalidation alert events.
/// Typical implementations publish to Service Bus or write to a notification table.
/// </summary>
public interface IEnrollmentNotificationHandler
{
    Task HandleRevalidationDueAsync(RevalidationDueEvent evt, CancellationToken ct = default);
}
