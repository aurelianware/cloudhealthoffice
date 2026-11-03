namespace ClaimsService.Services.RetroRate;

/// <summary>Where a reprocessing run is in its life.</summary>
public static class RetroRateReprocessingStatus
{
    /// <summary>Planned from an impact run; nothing changes until approved and executed.</summary>
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

/// <summary>What a reprocessing run does with one claim of its impact run.</summary>
public static class RetroRateItemAction
{
    /// <summary>Underpaid: an adjustment re-adjudicates it at the new rates and pays the difference.</summary>
    public const string PayDifference = "PayDifference";

    /// <summary>Overpaid, on a plan that opts in: an adjustment re-adjudicates it and the reversal recoups the difference.</summary>
    public const string Recoup = "Recoup";

    /// <summary>Overpaid, on a plan that has not opted in to recoupment: left as paid.</summary>
    public const string SkipRecoupmentNotOptedIn = "SkipRecoupmentNotOptedIn";

    /// <summary>The re-price did not decide; a person must look first.</summary>
    public const string SkipUndecided = "SkipUndecided";

    /// <summary>Approved but not yet paid (no 835): it cannot be adjusted; re-run the impact once it is paid.</summary>
    public const string SkipNotYetPaid = "SkipNotYetPaid";
}

/// <summary>The outcome of executing one item.</summary>
public static class RetroRateItemOutcome
{
    public const string Pending = "Pending";

    /// <summary>An adjustment was created; it re-adjudicates and then reverses and repays through the reversal run.</summary>
    public const string AdjustmentCreated = "AdjustmentCreated";

    /// <summary>This run already created the adjustment (a re-execution).</summary>
    public const string AlreadyAdjusted = "AlreadyAdjusted";

    /// <summary>The claim changed since the impact run (adjusted, voided, superseded); nothing was done.</summary>
    public const string ClaimChanged = "ClaimChanged";

    /// <summary>The adjustment service refused it (another open adjustment, depth limit, ...); see the message.</summary>
    public const string Refused = "Refused";

    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>One claim of a reprocessing run.</summary>
public class RetroRateReprocessingItem
{
    /// <summary>The claim document id the impact run read (the version to adjust).</summary>
    public string ClaimDocumentId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? BillingProviderNpi { get; set; }
    public string? BenefitPlanId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal NetPaidDelta { get; set; }

    /// <summary>A <see cref="RetroRateItemAction"/> value.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>A <see cref="RetroRateItemOutcome"/> value.</summary>
    public string Outcome { get; set; } = RetroRateItemOutcome.Pending;
    public string? AdjustmentId { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Turns an approved retro rate impact run into adjustments: each Underpaid
/// claim (and each Overpaid claim on a plan that opts in to recoupment) gets
/// an adjustment that re-adjudicates it at the current rates. The existing
/// adjustment lifecycle then reverses the original and pays the corrected
/// version on the 835 (CLP02=22 reversal plus the corrected claim).
/// </summary>
public class RetroRateReprocessingRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ImpactRunId { get; set; } = string.Empty;
    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public long Revision { get; set; }

    public string Status { get; set; } = RetroRateReprocessingStatus.Proposed;

    public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ExecutedBy { get; set; }
    public DateTime? ExecutionStartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CancelledBy { get; set; }

    /// <summary>Claims that get an adjustment, and what the impact run said they move.</summary>
    public int ClaimsToAdjust { get; set; }
    public decimal PlannedAdditionalPayment { get; set; }
    public decimal PlannedRecoupment { get; set; }

    /// <summary>Claims left alone, by <see cref="RetroRateItemAction"/>.</summary>
    public Dictionary<string, int> SkippedByAction { get; set; } = new(StringComparer.Ordinal);

    /// <summary>After execution: items by <see cref="RetroRateItemOutcome"/>.</summary>
    public Dictionary<string, int> CountsByOutcome { get; set; } = new(StringComparer.Ordinal);

    public List<RetroRateReprocessingItem> Items { get; set; } = [];

    /// <summary>
    /// Optimistic concurrency: each status change writes only while the stored
    /// revision is the one read, so two approvals or two executions cannot both win.
    /// </summary>
    public long ConcurrencyRevision { get; set; }
}

/// <summary>Body of <c>POST /api/v1/claims/retro-rate/reprocessing-runs</c>.</summary>
public class RetroRateReprocessingRunRequest
{
    public string ImpactRunId { get; set; } = string.Empty;

    /// <summary>Only these claims of the impact run (claim ids); all of them when empty.</summary>
    public List<string>? ClaimIds { get; set; }
}

/// <summary>Recoupment opt-in for one tenant, under <c>Adjudication:RetroRate:Tenants:{tenantId}</c>.</summary>
public class RetroRateTenantOptions
{
    /// <summary>Recoup overpayments on every plan of the tenant.</summary>
    public bool RecoupAllPlans { get; set; }

    /// <summary>Plans (plan id or plan code, as the claim carries it) whose overpayments are recouped.</summary>
    public List<string> RecoupPlans { get; set; } = [];

    public bool Recoups(string? planId) =>
        RecoupAllPlans
        || (!string.IsNullOrWhiteSpace(planId) && RecoupPlans.Contains(planId.Trim(), StringComparer.OrdinalIgnoreCase));
}
