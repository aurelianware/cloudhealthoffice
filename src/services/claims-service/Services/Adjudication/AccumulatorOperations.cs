using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

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
    /// <summary>Approved, Denied (PAUNAVAIL re-run only), StillPended, Skipped or Failed.</summary>
    public string Result { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class AccumulatorPendRerunResult
{
    public int Examined { get; set; }
    public int Approved { get; set; }
    /// <summary>PAUNAVAIL / PLANUNAVAIL re-run: the lookup answered definitively and the re-adjudication denied (CARC 197).</summary>
    public int Denied { get; set; }
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
    public const string PriorAuthRerunActorPrefix = "system:paunavail-rerun";
    private const int LookupBatch = 500;

    private readonly IClaimRepository _claims;
    private readonly IClaimApprovalReadjudicator? _readjudicator;
    private readonly IAccumulatorOutboxProcessor? _outbox;
    private readonly IAccumulatorCommitLookup? _lookup;
    private readonly IClaimVersionEventPublisher _versionEvents;
    private readonly IClaimEventPublisher _events;
    private readonly ILogger<AccumulatorOperationsService> _logger;
    private readonly IClaimWorkEventPublisher? _workEvents;

    public AccumulatorOperationsService(
        IClaimRepository claims,
        IClaimVersionEventPublisher versionEvents,
        IClaimEventPublisher events,
        ILogger<AccumulatorOperationsService> logger,
        IClaimApprovalReadjudicator? readjudicator = null,
        IAccumulatorOutboxProcessor? outbox = null,
        IAccumulatorCommitLookup? lookup = null,
        IClaimWorkEventPublisher? workEvents = null)
    {
        _workEvents = workEvents;
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
        var result = await RerunAllAsync(tenantId, pended, actor, AccumCommitRerun, ct);
        _logger.LogInformation(
            "AUDIT ACCUMCOMMIT bulk re-run by {Operator} (tenant {TenantId}): {Examined} examined, {Approved} approved, {StillPended} still pended, {Skipped} skipped, {Failed} failed",
            LogSanitizer.Sanitize(operatorId), LogSanitizer.Sanitize(tenantId), result.Examined, result.Approved, result.StillPended, result.Skipped, result.Failed);
        return result;
    }

    /// <summary>
    /// Re-runs up to <paramref name="limit"/> claims of the request tenant
    /// pended only for <see cref="Stages.BenefitCalculationStage.PriorAuthorizationUnavailablePendCode"/>
    /// or <see cref="Stages.BenefitCalculationStage.PlanUnavailablePendCode"/>
    /// (#1299 S1), once authorization-service / benefit-plan-service answer
    /// again. Each is re-adjudicated under its resolution lock exactly as an
    /// examiner approval would be: the reason is transient, so the lookup is
    /// repeated (always under claims-service's own service identity), never
    /// overridden. Passing → finalized Approved with its accumulator commit;
    /// a definitive NotFound / Invalid answer → the re-run's Deny stands
    /// (CARC 197, as on first adjudication); still unavailable → stays pended.
    /// </summary>
    public async Task<AccumulatorPendRerunResult> RerunPriorAuthUnavailablePendsAsync(
        string tenantId, string operatorId, int limit, CancellationToken ct)
    {
        if (_readjudicator is null) throw new InvalidOperationException("Approval re-adjudication is unavailable.");
        var actor = $"{PriorAuthRerunActorPrefix}:{operatorId}";
        var pended = await FairBatchAsync(PriorAuthRerunCodes, limit, ct);
        var result = await RerunAllAsync(tenantId, pended, actor, PriorAuthRerun, ct);
        _logger.LogInformation(
            "AUDIT PAUNAVAIL bulk re-run by {Operator} (tenant {TenantId}): {Examined} examined, {Approved} approved, {Denied} denied, {StillPended} still pended, {Skipped} skipped, {Failed} failed",
            LogSanitizer.Sanitize(operatorId), LogSanitizer.Sanitize(tenantId), result.Examined, result.Approved, result.Denied, result.StillPended, result.Skipped, result.Failed);
        return result;
    }

    private static readonly string[] PriorAuthRerunCodes =
    [
        Stages.BenefitCalculationStage.PriorAuthorizationUnavailablePendCode,
        Stages.BenefitCalculationStage.PlanUnavailablePendCode,
    ];

    /// <summary>
    /// Up to <paramref name="limit"/> claims across <paramref name="codes"/>,
    /// taken in turn from each code (least recently pended first within a
    /// code), so a backlog under one code cannot starve another; a code with
    /// fewer claims than its share leaves the rest of the batch to the others.
    /// </summary>
    private async Task<List<Claim>> FairBatchAsync(IReadOnlyList<string> codes, int limit, CancellationToken ct)
    {
        var queues = new List<Queue<Claim>>();
        foreach (var code in codes)
            queues.Add(new Queue<Claim>(await _claims.FindPendedByPendCodeAsync(code, limit, ct)));

        var batch = new List<Claim>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (batch.Count < limit && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (batch.Count >= limit) break;
                while (queue.Count > 0)
                {
                    var next = queue.Dequeue();
                    if (seen.Add(next.Id)) { batch.Add(next); break; }
                }
            }
        }
        return batch;
    }

    /// <summary>What distinguishes one bulk re-run from another.</summary>
    private sealed record RerunKind(string Name, IReadOnlyList<string> PendCodes, string Reason, bool DenyIsFinal);

    private static readonly RerunKind AccumCommitRerun = new(
        "ACCUMCOMMIT", [Stages.BenefitCalculationStage.DeferredCommitUnsupportedPendCode],
        "Re-run after benefit-plan-service upgrade (ACCUMCOMMIT)", DenyIsFinal: false);

    private static readonly RerunKind PriorAuthRerun = new(
        "PAUNAVAIL", PriorAuthRerunCodes,
        "Re-run after the prior-authorization / benefit-plan lookup recovered (PAUNAVAIL)", DenyIsFinal: true);

    private async Task<AccumulatorPendRerunResult> RerunAllAsync(
        string tenantId, IReadOnlyList<Claim> pended, string actor, RerunKind kind, CancellationToken ct)
    {
        var result = new AccumulatorPendRerunResult { Examined = pended.Count };
        foreach (var claim in pended)
        {
            ct.ThrowIfCancellationRequested();
            var item = await RerunOneAsync(tenantId, claim, actor, kind, ct);
            result.Claims.Add(item);
            switch (item.Result)
            {
                case "Approved": result.Approved++; break;
                case "Denied": result.Denied++; break;
                case "StillPended": result.StillPended++; break;
                case "Skipped": result.Skipped++; break;
                default: result.Failed++; break;
            }
        }
        return result;
    }

    private async Task<AccumulatorPendRerunItem> RerunOneAsync(string tenantId, Claim claim, string actor, RerunKind kind, CancellationToken ct)
    {
        AccumulatorPendRerunItem Item(string r, string? d = null) => new() { ClaimId = claim.Id, Result = r, Detail = d };

        if (claim.PendDetails?.AdditionalPendReasons is { Count: > 0 })
            return Item("Skipped", "the claim has other pend reasons: an examiner resolves it");

        var lockToken = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        if (!await _claims.TryAcquireResolutionLockAsync(
                tenantId, claim.Id, lockToken, actor, now, ClaimResolutionService.ResolutionLockDuration, ct))
            return Item("Skipped", "being resolved, or no longer Pended");
        var failed = false;
        var stillPended = false;
        AccumulatorPendRerunItem StillPended(string detail)
        {
            stillPended = true;
            return Item("StillPended", detail);
        }
        try
        {
            claim = await _claims.GetForAccumulatorOutboxAsync(tenantId, claim.Id, ct) ?? claim;
            if (claim.Status != ClaimStatus.Pended
                || !kind.PendCodes.Contains(claim.PendDetails?.PendCode ?? string.Empty, StringComparer.Ordinal)
                || claim.PendDetails?.AdditionalPendReasons is { Count: > 0 })
                return Item("Skipped", "the claim's pends changed");

            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var rerun = await _readjudicator!.ReadjudicateForApprovalAsync(tenantId, claim.Id, new ExaminerApproval
            {
                ExaminerId = actor,
                Reason = kind.Reason,
                CorrelationId = correlationId,
                ResolutionLockToken = lockToken,
                ReviewedPend = claim.PendDetails,
            }, ct);
            if (rerun.ResolutionLockLost)
                return Item("Failed", "the resolution lock was lost during the re-run");
            // Owner decision Q-15 (2026-10-11): the re-run denies only on the
            // PA check's definitive answer (CARC 197 after authorization-
            // service's NotFound / Invalid), and only when no check on this
            // run failed (#1299 re-verify B1). The orchestrator decides
            // "a check failed" from the run's structured state (CheckFailed:
            // flags set by the stages, never reason text). Any other Deny —
            // the benefit engine unavailable (999), NCCI in Deny mode, ... —
            // is left pended for an examiner.
            var transientFailure = rerun.CheckFailed;
            var definitivePriorAuthDenial = string.Equals(
                rerun.DenialReasonCode, Stages.BenefitCalculationStage.PriorAuthorizationRequiredCode, StringComparison.Ordinal);
            var deny = kind.DenyIsFinal && rerun.Outcome == ClaimAdjudicationOutcome.Deny
                       && definitivePriorAuthDenial && !transientFailure;
            if (kind.DenyIsFinal && rerun.Outcome == ClaimAdjudicationOutcome.Deny && !deny)
                return StillPended(transientFailure
                    ? $"Deny not finalized: a check could not run on this re-run ({rerun.DenialReason ?? rerun.Reason})"
                    : $"Deny not finalized: not the prior-authorization check's denial ({rerun.DenialReason ?? rerun.Reason}); an examiner resolves it");
            if (!deny && rerun.Outcome != ClaimAdjudicationOutcome.Pass)
                return StillPended($"{rerun.Outcome}: {rerun.Reason}");
            if (!deny && rerun.PreparedAccumulatorCommit is null)
                return StillPended("benefit-plan-service still prepares no accumulator commit");

            // From here the decision is made: finish it whatever the caller does.
            // Deny, not Denied: the re-run's denial is non-final until the
            // payment cycle remits it.
            var disposition = deny ? ClaimStatus.Deny : ClaimStatus.Approved;
            claim = await _claims.GetForAccumulatorOutboxAsync(tenantId, claim.Id, CancellationToken.None) ?? claim;
            var actedAt = DateTime.UtcNow;
            var denialReversal = deny
                ? await DenialAccumulatorReversal.ForDenialAsync(claim, claim.Status, actedAt, _lookup, _logger, CancellationToken.None)
                : null;
            var reviewed = claim.PendDetails;
            var sourceStatus = claim.Status;
            claim.LastUpdatedBy = actor;
            claim.Status = disposition;
            claim.VersionState = ClaimRepository.MapStatusToVersionState(disposition);
            claim.AdjudicatedDate = actedAt;
            claim.LastUpdatedDate = actedAt;
            claim.PendingExaminerApproval = null;
            claim.ResolutionLock = null;
            // As the examiner resolve path: an approval owes its commit; a
            // denial a non-terminal reversal only if the claim contributed
            // (a pended claim normally wrote nothing); Deny stays
            // reprocessable, so nothing is fenced.
            claim.PendingAccumulatorCommit = deny ? null : AccumulatorOutboxItem.ForCommit(rerun.PreparedAccumulatorCommit!, actedAt);
            claim.PendingAccumulatorReversal = denialReversal;
            if (claim.PendingAccumulatorCommit is { } queuedCommit)
                claim.LatestAccumulatorCommitId = queuedCommit.Id;
            claim.ExaminerResolutions.Add(new ExaminerResolutionRecord
            {
                Disposition = disposition.ToString(),
                ApproverIds = [actor],
                Reason = deny ? $"{kind.Reason}; denied on a definitive answer: {rerun.DenialReason ?? rerun.Reason}" : $"{kind.Reason}; nothing overridden",
                ApproverReasons = [null],
                PendFingerprint = PendDetails.ComputeFingerprint(reviewed),
                ReviewedPends = ExaminerApproval.ReviewedFrom(reviewed).Select(p => $"{p.Code}: {p.Reason}").ToList(),
                OverriddenPends = rerun.OverriddenPends.ToList(),
                RequestedAt = actedAt,
                ResolvedAt = actedAt,
            });
            // The finalized event is stored with the final write (outbox).
            claim.AddPendingClaimEvent(_events.PrepareClaimFinalized(claim, tenantId));
            var updated = await _claims.UpdateHoldingResolutionLockAsync(claim, lockToken, CancellationToken.None);
            if (updated is null) return Item("Failed", "the final write was refused (lock lost)");

            var outboxEntry = updated.PendingAccumulatorCommit is { } commitEntry
                ? (AccumulatorOutboxKind.Commit, commitEntry.Id)
                : updated.PendingAccumulatorReversal is { } reversalEntry
                    ? (AccumulatorOutboxKind.Reversal, reversalEntry.Id)
                    : ((AccumulatorOutboxKind, string)?)null;
            if (_outbox is not null && outboxEntry is { } entry)
            {
                try
                {
                    await _outbox.ProcessAsync(tenantId, updated.Id, entry.Item1, entry.Item2, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Claim {ClaimId}: accumulator commit not driven now; the outbox dispatcher retries it", LogSanitizer.Sanitize(updated.Id));
                }
            }
            await _versionEvents.PublishVersionResolvedAsync(
                updated, disposition.ToString(), $"{kind.Name} re-run", actor, correlationId, CancellationToken.None);
            await _events.PublishClaimFinalizedAsync(updated, tenantId);
            if (sourceStatus == ClaimStatus.Pended && _workEvents is not null)
                await _workEvents.PublishWorkResolvedAsync(updated, tenantId, disposition.ToString(), actor, CancellationToken.None);
            return deny ? Item("Denied", rerun.DenialReason ?? rerun.Reason) : Item("Approved");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Kind} re-run of claim {ClaimId} failed", kind.Name, LogSanitizer.Sanitize(claim.Id));
            failed = true;
            return Item("Failed", Describe(ex));
        }
        finally
        {
            // #1299 re-verify S1: a run that made no progress (failed, or the
            // re-run left the claim pended without re-stamping it) moves the
            // claim to the back of the queue, so it cannot hold the head of
            // every batch.
            if (failed || stillPended)
                await RestampAsync(tenantId, claim.Id, lockToken, now);
            await _claims.ReleaseResolutionLockAsync(tenantId, claim.Id, lockToken, CancellationToken.None);
        }
    }

    /// <summary>
    /// Re-stamps <see cref="PendDetails.PendedAt"/> of a still-Pended claim the
    /// re-run did not re-stamp, under the resolution lock this run holds. Best
    /// effort: a failure leaves the claim where it was.
    /// </summary>
    private async Task RestampAsync(string tenantId, string claimId, string lockToken, DateTime attemptStartedAt)
    {
        try
        {
            var fresh = await _claims.GetForAccumulatorOutboxAsync(tenantId, claimId, CancellationToken.None);
            if (fresh is not { Status: ClaimStatus.Pended, PendDetails: { } pend } || pend.PendedAt >= attemptStartedAt)
                return;
            pend.PendedAt = DateTime.UtcNow;
            await _claims.UpdateHoldingResolutionLockAsync(fresh, lockToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claim {ClaimId}: could not move the pend to the back of the re-run queue", LogSanitizer.Sanitize(claimId));
        }
    }

    /// <summary>
    /// What failed, for the operator's item result: the exception type and
    /// the claims-service frame it came from. No claim data.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var where = ex.StackTrace?
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("ClaimsService.", StringComparison.Ordinal));
        var what = ex.GetType().Name;
        return where is null ? what : $"{what} at {StripLineBreaks(where)}";
    }

    /// <summary>
    /// Removes CR and LF from a value that is stored or returned (not only
    /// logged). For log lines use <see cref="LogSanitizer.Sanitize(string?)"/>.
    /// </summary>
    private static string StripLineBreaks(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
