using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;

namespace ClaimsService.Services.Adjudication;

/// <summary>One claim of benefit-plan-service's accumulator commit lookup.</summary>
public sealed record AccumulatorCommitLookupItem(
    string ClaimId, string MemberId, string? SubscriberId, Guid BenefitPlanId, string PlanYear);

/// <summary>
/// Read-only: which claims the engine accumulator store holds a write record
/// for (benefit-plan-service <c>POST /api/v1/adjudication/accumulator-commits/lookup</c>).
/// </summary>
public interface IAccumulatorCommitLookup
{
    /// <summary>Claim id → recorded, for every claim given (at most 500 per call).</summary>
    Task<IReadOnlyDictionary<string, bool>> LookupCommitRecordsAsync(
        IReadOnlyList<AccumulatorCommitLookupItem> claims, CancellationToken ct = default);
}

/// <summary>A finalized claim the post-rollout sweep found without an accumulator write.</summary>
public sealed class AccumulatorSweepItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdatedDate { get; set; }
    public decimal Deductible { get; set; }
    public decimal PatientResponsibility { get; set; }
}

/// <summary>Result of the post-rollout accumulator sweep.</summary>
public sealed class AccumulatorSweepResult
{
    /// <summary>Finalized claims examined (with a non-zero cost share and a plan).</summary>
    public int Examined { get; set; }
    /// <summary>True when the window held more candidates than the limit: narrow it and run again.</summary>
    public bool Truncated { get; set; }
    public List<AccumulatorSweepItem> Missing { get; set; } = [];
}

/// <summary>One claim of the ACCUMCOMMIT bulk re-run.</summary>
public sealed class AccumulatorPendRerunItem
{
    public string ClaimId { get; set; } = string.Empty;
    /// <summary>Approved, StillPended, Skipped or Failed.</summary>
    public string Result { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class AccumulatorPendRerunResult
{
    public int Examined { get; set; }
    public int Approved { get; set; }
    public int StillPended { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<AccumulatorPendRerunItem> Claims { get; set; } = [];
}

/// <summary>
/// Operator tools for the accumulator outbox rollout:
/// <list type="bullet">
///   <item>the read-only sweep — finalized claims in a window with no
///     pending entry and no write record in the engine store (an old pod
///     dropped the entry, or a commit never happened);</item>
///   <item>the bulk re-run of claims pended <c>ACCUMCOMMIT</c> (claims-service
///     deployed before benefit-plan-service), so a misordered deploy does
///     not need one manual approval per claim.</item>
/// </list>
/// </summary>
public sealed class AccumulatorOperationsService
{
    public const string RerunActorPrefix = "system:accumcommit-rerun";
    private const int LookupBatch = 500;

    private readonly IClaimRepository _claims;
    private readonly IClaimApprovalReadjudicator? _readjudicator;
    private readonly IAccumulatorOutboxProcessor? _outbox;
    private readonly IAccumulatorCommitLookup? _lookup;
    private readonly IClaimVersionEventPublisher _versionEvents;
    private readonly IClaimEventPublisher _events;
    private readonly ILogger<AccumulatorOperationsService> _logger;

    public AccumulatorOperationsService(
        IClaimRepository claims,
        IClaimVersionEventPublisher versionEvents,
        IClaimEventPublisher events,
        ILogger<AccumulatorOperationsService> logger,
        IClaimApprovalReadjudicator? readjudicator = null,
        IAccumulatorOutboxProcessor? outbox = null,
        IAccumulatorCommitLookup? lookup = null)
    {
        _claims = claims;
        _versionEvents = versionEvents;
        _events = events;
        _logger = logger;
        _readjudicator = readjudicator;
        _outbox = outbox;
        _lookup = lookup;
    }

    /// <summary>
    /// Finalized claims of the request tenant last updated in the window
    /// whose deductible or patient responsibility is not zero, that hold no
    /// pending outbox entry and whose write the engine store has no record
    /// of. Read-only.
    /// </summary>
    public async Task<AccumulatorSweepResult> FindMissingCommitsAsync(DateTime from, DateTime to, int limit, CancellationToken ct)
    {
        if (_lookup is null) throw new InvalidOperationException("No accumulator commit lookup is configured.");
        var candidates = await _claims.FindFinalizedForAccumulatorSweepAsync(from, to, limit + 1, ct);
        var result = new AccumulatorSweepResult { Truncated = candidates.Count > limit };
        var counted = candidates.Take(limit)
            .Where(c => Guid.TryParse(c.BenefitPlanId, out _)
                        && ((c.AdjudicationResult?.DeductibleAmount ?? 0m) > 0m || (c.AdjudicationResult?.PatientResponsibility ?? 0m) > 0m))
            .ToList();
        result.Examined = counted.Count;
        foreach (var batch in counted.Chunk(LookupBatch))
        {
            var recorded = await _lookup.LookupCommitRecordsAsync(batch.Select(c => new AccumulatorCommitLookupItem(
                c.Id, c.MemberId, string.IsNullOrWhiteSpace(c.SubscriberId) ? c.MemberId : c.SubscriberId,
                Guid.Parse(c.BenefitPlanId!), c.ServiceDateFrom.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(), ct);
            foreach (var c in batch)
            {
                if (recorded.TryGetValue(c.Id, out var has) && has) continue;
                result.Missing.Add(new AccumulatorSweepItem
                {
                    ClaimId = c.Id,
                    ClaimNumber = c.ClaimNumber,
                    Status = c.Status.ToString(),
                    LastUpdatedDate = c.LastUpdatedDate,
                    Deductible = c.AdjudicationResult?.DeductibleAmount ?? 0m,
                    PatientResponsibility = c.AdjudicationResult?.PatientResponsibility ?? 0m,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Re-runs up to <paramref name="limit"/> claims of the request tenant
    /// pended only for <see cref="Stages.BenefitCalculationStage.DeferredCommitUnsupportedPendCode"/>.
    /// Each is re-adjudicated under its resolution lock exactly as an
    /// examiner approval would be — the ACCUMCOMMIT reason is transient, so
    /// it is re-checked, never overridden, and nothing else is overridden.
    /// A re-run that passes is finalized Approved with its accumulator
    /// commit (outbox), audited as a system resolution; one that pends again
    /// keeps its new pends for an examiner. Claims with any other pend
    /// reason are left for an examiner.
    /// </summary>
    public async Task<AccumulatorPendRerunResult> RerunAccumulatorCommitPendsAsync(
        string tenantId, string operatorId, int limit, CancellationToken ct)
    {
        if (_readjudicator is null) throw new InvalidOperationException("Approval re-adjudication is unavailable.");
        var actor = $"{RerunActorPrefix}:{operatorId}";
        var pended = await _claims.FindPendedByPendCodeAsync(Stages.BenefitCalculationStage.DeferredCommitUnsupportedPendCode, limit, ct);
        var result = new AccumulatorPendRerunResult { Examined = pended.Count };
        foreach (var claim in pended)
        {
            ct.ThrowIfCancellationRequested();
            var item = await RerunOneAsync(tenantId, claim, actor, ct);
            result.Claims.Add(item);
            switch (item.Result)
            {
                case "Approved": result.Approved++; break;
                case "StillPended": result.StillPended++; break;
                case "Skipped": result.Skipped++; break;
                default: result.Failed++; break;
            }
        }
        _logger.LogInformation(
            "AUDIT ACCUMCOMMIT bulk re-run by {Operator} (tenant {TenantId}): {Examined} examined, {Approved} approved, {StillPended} still pended, {Skipped} skipped, {Failed} failed",
            Sanitize(operatorId), Sanitize(tenantId), result.Examined, result.Approved, result.StillPended, result.Skipped, result.Failed);
        return result;
    }

    private async Task<AccumulatorPendRerunItem> RerunOneAsync(string tenantId, Claim claim, string actor, CancellationToken ct)
    {
        AccumulatorPendRerunItem Item(string r, string? d = null) => new() { ClaimId = claim.Id, Result = r, Detail = d };

        if (claim.PendDetails?.AdditionalPendReasons is { Count: > 0 })
            return Item("Skipped", "the claim has other pend reasons: an examiner resolves it");

        var lockToken = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        if (!await _claims.TryAcquireResolutionLockAsync(
                tenantId, claim.Id, lockToken, actor, now, Controllers.ClaimsController.ResolutionLockDuration, ct))
            return Item("Skipped", "being resolved, or no longer Pended");
        try
        {
            claim = await _claims.GetForAccumulatorOutboxAsync(tenantId, claim.Id, ct) ?? claim;
            if (claim.Status != ClaimStatus.Pended
                || !string.Equals(claim.PendDetails?.PendCode, Stages.BenefitCalculationStage.DeferredCommitUnsupportedPendCode, StringComparison.Ordinal)
                || claim.PendDetails?.AdditionalPendReasons is { Count: > 0 })
                return Item("Skipped", "the claim's pends changed");

            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var rerun = await _readjudicator!.ReadjudicateForApprovalAsync(tenantId, claim.Id, new ExaminerApproval
            {
                ExaminerId = actor,
                Reason = "Re-run after benefit-plan-service upgrade (ACCUMCOMMIT)",
                CorrelationId = correlationId,
                ResolutionLockToken = lockToken,
                ReviewedPend = claim.PendDetails,
            }, ct);
            if (rerun.ResolutionLockLost)
                return Item("Failed", "the resolution lock was lost during the re-run");
            if (rerun.Outcome != ClaimAdjudicationOutcome.Pass)
                return Item("StillPended", $"{rerun.Outcome}: {rerun.Reason}");
            if (rerun.PreparedAccumulatorCommit is null)
                return Item("StillPended", "benefit-plan-service still prepares no accumulator commit");

            // From here the decision is made: finish it whatever the caller does.
            claim = await _claims.GetForAccumulatorOutboxAsync(tenantId, claim.Id, CancellationToken.None) ?? claim;
            var actedAt = DateTime.UtcNow;
            var reviewed = claim.PendDetails;
            claim.LastUpdatedBy = actor;
            claim.Status = ClaimStatus.Approved;
            claim.VersionState = ClaimRepository.MapStatusToVersionState(ClaimStatus.Approved);
            claim.AdjudicatedDate = actedAt;
            claim.LastUpdatedDate = actedAt;
            claim.PendingExaminerApproval = null;
            claim.ResolutionLock = null;
            claim.PendingAccumulatorCommit = AccumulatorOutboxItem.ForCommit(rerun.PreparedAccumulatorCommit, actedAt);
            claim.PendingAccumulatorReversal = null;
            claim.ExaminerResolutions.Add(new ExaminerResolutionRecord
            {
                Disposition = nameof(ClaimStatus.Approved),
                ApproverIds = [actor],
                Reason = "Re-run after benefit-plan-service upgrade (ACCUMCOMMIT); nothing overridden",
                ApproverReasons = [null],
                PendFingerprint = PendDetails.ComputeFingerprint(reviewed),
                ReviewedPends = ExaminerApproval.ReviewedFrom(reviewed).Select(p => $"{p.Code}: {p.Reason}").ToList(),
                OverriddenPends = rerun.OverriddenPends.ToList(),
                RequestedAt = actedAt,
                ResolvedAt = actedAt,
            });
            var updated = await _claims.UpdateHoldingResolutionLockAsync(claim, lockToken, CancellationToken.None);
            if (updated is null) return Item("Failed", "the final write was refused (lock lost)");

            if (_outbox is not null && updated.PendingAccumulatorCommit is { } entry)
            {
                try
                {
                    await _outbox.ProcessAsync(tenantId, updated.Id, AccumulatorOutboxKind.Commit, entry.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Claim {ClaimId}: accumulator commit not driven now; the outbox dispatcher retries it", Sanitize(updated.Id));
                }
            }
            await _versionEvents.PublishVersionResolvedAsync(
                updated, nameof(ClaimStatus.Approved), "ACCUMCOMMIT re-run", actor, correlationId, CancellationToken.None);
            await _events.PublishClaimFinalizedAsync(updated, tenantId);
            return Item("Approved");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ACCUMCOMMIT re-run of claim {ClaimId} failed", Sanitize(claim.Id));
            return Item("Failed", ex.GetType().Name);
        }
        finally
        {
            await _claims.ReleaseResolutionLockAsync(tenantId, claim.Id, lockToken, CancellationToken.None);
        }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
