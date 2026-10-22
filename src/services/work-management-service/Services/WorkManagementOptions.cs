namespace WorkManagementService.Services;

/// <summary>Binds from <c>WorkManagement</c>.</summary>
public sealed class WorkManagementOptions
{
    public const string SectionName = "WorkManagement";

    /// <summary>How long a Get Next lease lasts (renewable).</summary>
    public int LeaseMinutes { get; set; } = 30;

    /// <summary>How long a supervisor assignment holds the item for the assignee.</summary>
    public int AssignedLeaseMinutes { get; set; } = 480;

    /// <summary>Candidates read per queue per Get Next round.</summary>
    public int CandidatesPerQueue { get; set; } = 25;

    /// <summary>Get Next re-reads candidates this many times when every candidate was taken concurrently.</summary>
    public int GetNextRounds { get; set; } = 3;

    public ReconcileOptions Reconcile { get; set; } = new();

    /// <summary>Periodic reconcile of enrollment exceptions (their only path into work today).</summary>
    public EnrollmentReconcileOptions EnrollmentReconcile { get; set; } = new();

    /// <summary>
    /// Users given a primary membership in every claim queue of a tenant that
    /// has no memberships for them yet, checked when this process first uses
    /// the tenant. For demo environments, so the presenter can press Get Next
    /// without setting up queues first; leave empty in real deployments.
    /// </summary>
    public List<string> SeedMemberUserIds { get; set; } = new();

    /// <summary>
    /// Re-score open items this often (priority is deadline- and age-driven, so
    /// it rises while an item waits). 0 disables.
    /// </summary>
    public int RescoreIntervalMinutes { get; set; } = 15;

    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(Math.Max(1, LeaseMinutes));
    public TimeSpan AssignedLeaseDuration => TimeSpan.FromMinutes(Math.Max(1, AssignedLeaseMinutes));
}

/// <summary>Periodic reconcile against claims-service.</summary>
public sealed class ReconcileOptions
{
    /// <summary>
    /// Run the periodic reconcile. Null (default) = automatic: on when this
    /// service cannot receive claim events (no Kafka:BootstrapServers, e.g. a
    /// demo whose bus is in-memory), off when it can. The admin endpoint
    /// reconciles on demand either way.
    /// </summary>
    public bool? Enabled { get; set; }

    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Snapshot page size requested from claims-service.</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Tenants to reconcile besides those already configured here (first use seeds them).</summary>
    public List<string> Tenants { get; set; } = new();
}

/// <summary>
/// Periodic reconcile against enrollment-import-service. On by default:
/// enrollment exceptions have no event stream yet, so this is how they become
/// (and stop being) work items. Tenants are the same as the claims reconcile's.
/// </summary>
public sealed class EnrollmentReconcileOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 2;
    public int PageSize { get; set; } = 500;
}
