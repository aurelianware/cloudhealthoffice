using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Retro rate reprocessing, step 3: propose, approve and execute the
/// adjustments an impact run calls for. Underpaid claims are always adjusted
/// (the difference is paid); Overpaid claims only on plans that opt in to
/// recoupment (<see cref="RetroRateTenantOptions"/>). Execution goes through
/// <see cref="IClaimAdjustmentService"/>, so each adjustment re-adjudicates
/// the claim at the current rates and the reversal run sends the CLP02=22
/// reversal and the corrected claim on the 835.
/// </summary>
public interface IRetroRateReprocessingService
{
    Task<RetroRateReprocessingRun> ProposeAsync(string tenantId, RetroRateReprocessingRunRequest request, string actorId, CancellationToken ct);
    Task<RetroRateReprocessingRun> ApproveAsync(string tenantId, string runId, string actorId, CancellationToken ct);
    Task<RetroRateReprocessingRun> ExecuteAsync(string tenantId, string runId, string actorId, string? correlationId, CancellationToken ct);
    Task<RetroRateReprocessingRun> CancelAsync(string tenantId, string runId, string actorId, CancellationToken ct);
}

/// <summary>A request the run's state or the rules do not allow (409 / 400 / 404 at the API).</summary>
public sealed class RetroRateReprocessingException(RetroRateReprocessingException.Kind kind, string message) : Exception(message)
{
    public enum Kind { NotFound, Invalid, Conflict }
    public Kind Reason { get; } = kind;
}

public sealed class RetroRateReprocessingService : IRetroRateReprocessingService
{
    private readonly IRetroRateImpactRepository _impact;
    private readonly IRetroRateReprocessingRepository _runs;
    private readonly IClaimRepository _claims;
    private readonly IClaimAdjustmentService _adjustments;
    private readonly RetroRateOptions _options;
    private readonly ILogger<RetroRateReprocessingService> _logger;

    public RetroRateReprocessingService(
        IRetroRateImpactRepository impact,
        IRetroRateReprocessingRepository runs,
        IClaimRepository claims,
        IClaimAdjustmentService adjustments,
        IOptions<RetroRateOptions> options,
        ILogger<RetroRateReprocessingService> logger)
    {
        _impact = impact;
        _runs = runs;
        _claims = claims;
        _adjustments = adjustments;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RetroRateReprocessingRun> ProposeAsync(
        string tenantId, RetroRateReprocessingRunRequest request, string actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ImpactRunId))
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Invalid, "impactRunId is required.");

        var impact = await _impact.GetRunAsync(tenantId, request.ImpactRunId.Trim(), ct).ConfigureAwait(false)
            ?? throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.NotFound, $"Impact run '{request.ImpactRunId}' not found.");
        var results = await _impact.ListResultsAsync(tenantId, impact.Id, direction: null, Math.Max(1, _options.MaxClaimsPerRun), ct)
            .ConfigureAwait(false);

        var only = request.ClaimIds is { Count: > 0 }
            ? new HashSet<string>(request.ClaimIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()), StringComparer.Ordinal)
            : null;

        var run = Plan(impact, only is null ? results : results.Where(r => only.Contains(r.ClaimId)).ToList(), _options.For(tenantId));
        run.TenantId = tenantId;
        run.CreatedBy = actorId;
        run.CreatedAtUtc = DateTime.UtcNow;
        if (run.ClaimsToAdjust == 0)
            run.Status = RetroRateReprocessingStatus.Completed; // nothing to approve: an empty plan is a finished record
        await _runs.CreateAsync(run, ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>Which claims get an adjustment, and why the rest do not. Pure.</summary>
    internal static RetroRateReprocessingRun Plan(
        RetroRateImpactRun impact, IReadOnlyList<RetroRateImpactResult> results, RetroRateTenantOptions tenant)
    {
        var run = new RetroRateReprocessingRun
        {
            ImpactRunId = impact.Id,
            FeeScheduleId = impact.FeeScheduleId,
            FeeScheduleName = impact.FeeScheduleName,
            Revision = impact.NewRevision,
        };

        foreach (var r in results.Where(r => r.Direction != RetroRateDirection.Unchanged).OrderByDescending(r => r.AbsoluteNetPaidDelta))
        {
            var action = r.Direction switch
            {
                RetroRateDirection.Undecided => RetroRateItemAction.SkipUndecided,
                _ when string.Equals(r.ClaimStatus, nameof(ClaimStatus.Approved), StringComparison.Ordinal) => RetroRateItemAction.SkipNotYetPaid,
                RetroRateDirection.Underpaid => RetroRateItemAction.PayDifference,
                RetroRateDirection.Overpaid when tenant.Recoups(r.BenefitPlanId) => RetroRateItemAction.Recoup,
                _ => RetroRateItemAction.SkipRecoupmentNotOptedIn,
            };
            var adjusts = action is RetroRateItemAction.PayDifference or RetroRateItemAction.Recoup;
            run.Items.Add(new RetroRateReprocessingItem
            {
                ClaimDocumentId = r.ClaimDocumentId,
                ClaimId = r.ClaimId,
                ClaimNumber = r.ClaimNumber,
                BillingProviderNpi = r.BillingProviderNpi,
                BenefitPlanId = r.BenefitPlanId,
                Direction = r.Direction,
                NetPaidDelta = r.NetPaidDelta,
                Action = action,
                Outcome = adjusts ? RetroRateItemOutcome.Pending : RetroRateItemOutcome.Skipped,
            });
        }

        run.ClaimsToAdjust = run.Items.Count(i => i.Outcome == RetroRateItemOutcome.Pending);
        run.PlannedAdditionalPayment = run.Items.Where(i => i.Action == RetroRateItemAction.PayDifference).Sum(i => i.NetPaidDelta);
        run.PlannedRecoupment = -run.Items.Where(i => i.Action == RetroRateItemAction.Recoup).Sum(i => i.NetPaidDelta);
        run.SkippedByAction = run.Items
            .Where(i => i.Outcome == RetroRateItemOutcome.Skipped)
            .GroupBy(i => i.Action, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return run;
    }

    public async Task<RetroRateReprocessingRun> ApproveAsync(string tenantId, string runId, string actorId, CancellationToken ct)
    {
        var run = await Load(tenantId, runId, ct).ConfigureAwait(false);
        Require(run, RetroRateReprocessingStatus.Proposed, "approved");
        if (_options.RequireSecondApprover && string.Equals(run.CreatedBy, actorId, StringComparison.Ordinal))
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict,
                "A reprocessing run must be approved by someone other than the person who proposed it.");

        var expected = run.ConcurrencyRevision;
        run.Status = RetroRateReprocessingStatus.Approved;
        run.ApprovedBy = actorId;
        run.ApprovedAtUtc = DateTime.UtcNow;
        return await Save(run, expected, ct).ConfigureAwait(false);
    }

    public async Task<RetroRateReprocessingRun> CancelAsync(string tenantId, string runId, string actorId, CancellationToken ct)
    {
        var run = await Load(tenantId, runId, ct).ConfigureAwait(false);
        if (run.Status is not (RetroRateReprocessingStatus.Proposed or RetroRateReprocessingStatus.Approved))
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict,
                $"A {run.Status} reprocessing run cannot be cancelled.");
        var expected = run.ConcurrencyRevision;
        run.Status = RetroRateReprocessingStatus.Cancelled;
        run.CancelledBy = actorId;
        run.CompletedAtUtc = DateTime.UtcNow;
        return await Save(run, expected, ct).ConfigureAwait(false);
    }

    public async Task<RetroRateReprocessingRun> ExecuteAsync(
        string tenantId, string runId, string actorId, string? correlationId, CancellationToken ct)
    {
        var run = await Load(tenantId, runId, ct).ConfigureAwait(false);
        // An Executing run that stopped part way (a crash, a timeout) may be
        // executed again: adjustments are idempotent per run and claim.
        if (run.Status is not (RetroRateReprocessingStatus.Approved or RetroRateReprocessingStatus.Executing))
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict,
                run.Status == RetroRateReprocessingStatus.Proposed
                    ? "The reprocessing run must be approved before it is executed."
                    : $"A {run.Status} reprocessing run cannot be executed.");

        var expected = run.ConcurrencyRevision;
        run.Status = RetroRateReprocessingStatus.Executing;
        run.ExecutedBy = actorId;
        run.ExecutionStartedAtUtc ??= DateTime.UtcNow;
        run = await Save(run, expected, ct).ConfigureAwait(false);

        foreach (var item in run.Items.Where(i => i.Outcome is RetroRateItemOutcome.Pending or RetroRateItemOutcome.Failed))
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteItemAsync(run, item, tenantId, actorId, correlationId, ct).ConfigureAwait(false);
        }

        expected = run.ConcurrencyRevision;
        run.Status = RetroRateReprocessingStatus.Completed;
        run.CompletedAtUtc = DateTime.UtcNow;
        run.CountsByOutcome = run.Items
            .GroupBy(i => i.Outcome, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        run = await Save(run, expected, ct).ConfigureAwait(false);

        _logger.LogInformation("Retro rate reprocessing run {RunId}: {Created} adjustments created of {ToAdjust}",
            run.Id, run.CountsByOutcome.GetValueOrDefault(RetroRateItemOutcome.AdjustmentCreated), run.ClaimsToAdjust);
        return run;
    }

    private async Task ExecuteItemAsync(
        RetroRateReprocessingRun run, RetroRateReprocessingItem item, string tenantId, string actorId, string? correlationId, CancellationToken ct)
    {
        try
        {
            var claim = await _claims.GetByIdAsync(item.ClaimDocumentId).ConfigureAwait(false);
            if (claim is null || claim.TenantId != tenantId)
            {
                item.Outcome = RetroRateItemOutcome.ClaimChanged;
                item.Message = "The claim version the impact run read is no longer found.";
                return;
            }
            if (claim.SupersededAt is not null || claim.VersionState == ClaimVersionState.Adjusted
                || claim.Status is not (ClaimStatus.Paid or ClaimStatus.PartiallyPaid))
            {
                item.Outcome = RetroRateItemOutcome.ClaimChanged;
                item.Message = $"The claim changed since the impact run (status {claim.Status}{(claim.SupersededAt is null ? "" : ", superseded")}); run the impact again.";
                return;
            }

            var request = new ClaimAdjustmentRequest
            {
                AdjustmentReason = Truncate(
                    $"Retro rate change: fee schedule {run.FeeScheduleName} revision {run.Revision} " +
                    $"({(item.Action == RetroRateItemAction.Recoup ? "recoupment" : "additional payment")})", 500),
                Notes = Truncate($"Retro rate reprocessing run {run.Id}, impact run {run.ImpactRunId}, schedule {run.FeeScheduleId}. " +
                                 $"Expected net {item.NetPaidDelta:0.00}.", 2000),
                CorrectedClaim = ClaimCorrectionDraft.From(claim),
            };
            var result = await _adjustments.CreateAdjustmentAsync(
                claim.Id, request, $"retro-rate:{run.Id}:{claim.Id}", tenantId, actorId, correlationId, ct).ConfigureAwait(false);

            item.AdjustmentId = result.Adjustment?.Id;
            (item.Outcome, item.Message) = result.Outcome switch
            {
                ClaimAdjustmentOutcome.Created => (RetroRateItemOutcome.AdjustmentCreated, (string?)null),
                ClaimAdjustmentOutcome.AlreadyExists => (RetroRateItemOutcome.AlreadyAdjusted, null),
                ClaimAdjustmentOutcome.SubmissionFailed => (RetroRateItemOutcome.Failed, result.Message),
                _ => (RetroRateItemOutcome.Refused, $"{result.Outcome}: {result.Message}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retro rate adjustment failed for claim {ClaimId}", LogSanitizer.Sanitize(item.ClaimId));
            item.Outcome = RetroRateItemOutcome.Failed;
            item.Message = ex.GetType().Name;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<RetroRateReprocessingRun> Load(string tenantId, string runId, CancellationToken ct) =>
        await _runs.GetAsync(tenantId, runId, ct).ConfigureAwait(false)
        ?? throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.NotFound, $"Reprocessing run '{runId}' not found.");

    private static void Require(RetroRateReprocessingRun run, string status, string verb)
    {
        if (run.Status != status)
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict,
                $"Only a {status} reprocessing run can be {verb}; this one is {run.Status}.");
    }

    private async Task<RetroRateReprocessingRun> Save(RetroRateReprocessingRun run, long expected, CancellationToken ct)
    {
        if (!await _runs.TryReplaceAsync(run, expected, ct).ConfigureAwait(false))
            throw new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict,
                "The reprocessing run changed while this request was working on it; read it again.");
        return run;
    }
}
