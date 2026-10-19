using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication;

namespace ClaimsService.Repositories;

/// <summary>
/// Result of a repository call that writes <see cref="ClaimStatus"/> through a
/// precedence guard (<see cref="ClaimRepository.BlocksSynchronousWriteback"/>).
/// <see cref="Suppressed"/> means the row exists but the guard blocked the
/// requested transition — <see cref="PersistedStatus"/> reports what the claim's
/// status actually is now, so the caller can react (e.g. score against Pended
/// instead of the outcome it asked for) instead of silently believing its write
/// won.
/// </summary>
public enum StatusWriteOutcome
{
    NotFound,
    Applied,
    Suppressed,
}

/// <summary>See <see cref="StatusWriteOutcome"/>. <see cref="PersistedStatus"/> is
/// null only for <see cref="StatusWriteOutcome.NotFound"/>.</summary>
public readonly record struct StatusWriteResult(StatusWriteOutcome Outcome, ClaimStatus? PersistedStatus)
{
    public static readonly StatusWriteResult NotFoundResult = new(StatusWriteOutcome.NotFound, null);
}

public interface IClaimRepository
{
    Task<Claim?> GetByIdAsync(string id);
    Task<Claim?> GetByClaimNumberAsync(string claimNumber);
    Task<IEnumerable<Claim>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);

    Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchWithCountAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);

    /// <summary>
    /// Search by <see cref="ClaimCriteria"/> (current versions only, newest
    /// first): the claims list's reference and Insights drill-down filters.
    /// </summary>
    Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchByCriteriaAsync(ClaimCriteria criteria, int page, int pageSize)
        => throw new NotSupportedException("Searching by claim reference or Insights criteria needs the MongoDB claim store.");

    /// <summary>
    /// How many current claims (header or any line, 837 REF*G1) carry each of
    /// these prior authorization numbers; numbers no claim carries are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetAuthorizationUsageAsync(IReadOnlyCollection<string> authorizationNumbers, CancellationToken ct = default)
        => throw new NotSupportedException("Authorization usage needs the MongoDB claim store.");

    /// <summary>Claims Insights for claims adjudicated in [from, to), and the open inventory at <paramref name="asOf"/>.</summary>
    Task<ClaimInsights> GetInsightsAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf, CancellationToken ct = default)
        => throw new NotSupportedException("Claims Insights needs the MongoDB claim store.");

    Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchByIdsAsync(
        IReadOnlyCollection<string> claimIds,
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        int page,
        int pageSize);

    /// <summary>
    /// Member-scoped search with the filters the portal Member Details dialog
    /// exposes: date range, status, provider, claim type, amount range. Always
    /// requires a memberId — this method exists so the v1 member endpoint has a
    /// single repository path and so amountRange/claimType filters don't force
    /// a signature change on the wider SearchAsync.
    /// Returns (matching page, totalCount) where totalCount reflects the full
    /// result set across all pages so the portal can paginate.
    /// </summary>
    Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchForMemberAsync(
        string memberId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        string? providerNPI,
        ClaimType? claimType,
        decimal? amountMin,
        decimal? amountMax,
        int page,
        int pageSize);

    Task<ClaimsSummary> GetClaimsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);

    /// <summary>
    /// Aggregate finalized claim cost-share amounts by accumulator type and network tier
    /// for a member (Individual scope) or all family members (Family scope).
    ///
    /// Called by the Redis accumulator service on a cache miss to rebuild from claim history.
    /// The plan year is converted to a Jan 1 – Dec 31 date range for standard calendar plans.
    /// </summary>
    /// <param name="ownerId">memberId for Individual scope; subscriberId for Family scope.</param>
    /// <param name="scope">"Individual" or "Family"</param>
    Task<AccumulatorTotalsResponse> GetAccumulatorTotalsAsync(
        string ownerId,
        string scope,
        string benefitPlanId,
        string planYear,
        CancellationToken ct = default);

    Task<Claim> CreateAsync(Claim claim);
    Task<Claim> UpdateAsync(Claim claim);
    Task DeleteAsync(string id);

    // ── Versioning surface (5.1) ─────────────────────────────────────────
    // Mirrors IProviderRepository / IBenefitPlanRepository. asOf is the
    // time-travel pivot; passing DateTime.UtcNow returns the current head
    // version. Capability 5.6 (network/credentialing-as-of-service-date)
    // is the first consumer of asOf semantics on claims.

    /// <summary>
    /// Latest non-Draft version of the chain identified by
    /// <paramref name="claimVersionId"/> in effect at <paramref name="asOf"/>.
    /// "In effect" means <c>PublishedAt &lt;= asOf</c> and either
    /// <c>SupersededAt</c> is null or <c>SupersededAt &gt; asOf</c>. Returns
    /// null when no such version exists.
    /// </summary>
    Task<Claim?> GetLatestVersionAsync(string claimVersionId, DateTime asOf);

    /// <summary>Look up a single version by its per-row <c>Id</c>.</summary>
    Task<Claim?> GetVersionAsync(string claimVersionId, string versionId);

    /// <summary>
    /// Newest-first list of every version for the chain identified by
    /// <paramref name="claimVersionId"/>, paginated with a continuation
    /// token. Mirrors <c>IProviderRepository.ListVersionsAsync</c>.
    /// </summary>
    Task<(IReadOnlyList<Claim> Items, string? ContinuationToken)> ListVersionsAsync(
        string claimVersionId, int pageSize, string? continuationToken);

    /// <summary>
    /// Projection-metadata bypass for adjudication writes. Patches only the
    /// adjudication-related fields on the head version of
    /// <paramref name="claimVersionId"/> for <paramref name="tenantId"/>;
    /// does NOT create a new version row and does NOT trip the
    /// <see cref="UpdateAsync"/> terminal-state guard.
    ///
    /// 5th instance of the projection-metadata bypass pattern (Provider 5.4.5
    /// integrity, Provider 5.6 credentialing, Provider 5.7+ panel-gating, BP
    /// 5.5 network tiers). Same justification: adjudication state is
    /// operationally distinct from claim identity, and each adjudication run
    /// shouldn't produce a new version. Adjustments DO produce new versions
    /// via <see cref="UpdateAsync"/>; this bypass is for the routine path.
    ///
    /// <para>
    /// 5.7 — <paramref name="pendDetails"/> is an optional fourth field on
    /// the projection. <see cref="PendDetails"/> is "operationally distinct
    /// from claim identity" by the same logic as
    /// <see cref="AdjudicationResult"/> and is the deterministic
    /// edit-failure surface populated by NCCI / MUE / future authorization
    /// stages. Pass null when no pend reason applies (the current head's
    /// PendDetails is left untouched). Pass non-null to project the
    /// snapshot — the AI examiner (5.9) and remittance generator (5.10)
    /// read from this field on the head row.
    /// </para>
    ///
    /// <para>
    /// <paramref name="isPend"/> (pend-persistence defect fix) — when true,
    /// this call ALSO patches <c>ClaimStatus</c> to <see cref="ClaimStatus.Pended"/>,
    /// subject to one precedence rule: a claim already at a later-stage
    /// disposition (<see cref="ClaimRepository.IsFinalDisposition"/> —
    /// Approved, Denied, Paid, PartiallyPaid, Voided) is never downgraded
    /// back to Pended, in case this async projection lands after another
    /// write path (remittance, a void, or an examiner override) already
    /// finalized the claim. Re-pending an
    /// already-<c>Pended</c> claim (a re-adjudication run refreshing
    /// <paramref name="pendDetails"/>) is allowed — Pended is not a final
    /// disposition. When <paramref name="resolvedStatus"/> is supplied, this
    /// call also projects async Pass/Deny/Reject outcomes through the same
    /// guarded status transition used by <c>TryTransitionStatusAsync</c>,
    /// so async-only adjudication runs become observable without overwriting
    /// an already Pended or final claim.
    /// </para>
    ///
    /// <para>
    /// <paramref name="requiredResolutionLockToken"/> (PR #1278 follow-up 1)
    /// — set by an examiner-approval re-run: the write lands only while the
    /// row still holds that examiner resolution lock
    /// (<c>ResolutionLock.Token</c>), checked by the write itself. A re-run
    /// that outlived its lock (another examiner took over and may have
    /// finalized the claim) writes nothing and this returns false.
    /// </para>
    ///
    /// <para>
    /// <paramref name="pendingAccumulatorCommit"/> — the accumulator outbox
    /// entry a passing claim owes: written by the same conditional status
    /// write that sets it Approved (and only if that write applies), so the
    /// commit exists exactly when the claim is finally adjudicated.
    /// </para>
    ///
    /// Returns true on success, false when no head row was found for the
    /// chain (or the resolution lock is no longer held).
    /// </summary>
    Task<bool> UpdateAdjudicationProjectionAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        IReadOnlyList<LineAdjudicationResult> lineResults,
        CancellationToken ct = default,
        PendDetails? pendDetails = null,
        bool isPend = false,
        ClaimStatus? resolvedStatus = null,
        string? resolvedBenefitPlanId = null,
        string? requiredResolutionLockToken = null,
        AccumulatorOutboxItem? pendingAccumulatorCommit = null);

    // ── accumulator outbox (see Claim.PendingAccumulatorCommit) ─────────

    /// <summary>Up to <paramref name="limit"/> claims of every tenant with an outbox entry due at <paramref name="nowMs"/> (Unix ms).</summary>
    Task<IReadOnlyList<Claim>> FindDueAccumulatorOutboxAsync(long nowMs, int limit, CancellationToken ct = default);

    /// <summary>The oldest outbox entry's creation time (Unix ms), any tenant; null when the outbox is empty.</summary>
    Task<long?> OldestAccumulatorOutboxAsync(CancellationToken ct = default);

    /// <summary>The claim, read for the outbox (explicit tenant, no request context needed).</summary>
    Task<Claim?> GetForAccumulatorOutboxAsync(string tenantId, string claimId, CancellationToken ct = default);

    /// <summary>
    /// Clears the outbox entry only while it is still <paramref name="itemId"/>
    /// (a newer entry is never cleared), recording <paramref name="clampReview"/>
    /// in the same write when given. False when it no longer matches.
    /// </summary>
    Task<bool> CompleteAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        AccumulatorClampReview? clampReview = null, CancellationToken ct = default);

    /// <summary>Records a failed attempt and the next due time, only while the entry is still <paramref name="itemId"/>.</summary>
    Task<bool> RescheduleAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        int attempts, DateTime nextAttemptAt, string? lastError, CancellationToken ct = default);

    // ── claim-event outbox (see Claim.PendingClaimEvents) ───────────────

    /// <summary>Up to <paramref name="limit"/> claims of every tenant with a pending claim event due at <paramref name="nowMs"/> (Unix ms).</summary>
    Task<IReadOnlyList<Claim>> FindDueClaimEventsAsync(long nowMs, int limit, CancellationToken ct = default);

    /// <summary>The oldest pending claim event's creation time (Unix ms), any tenant; null when there is none.</summary>
    Task<long?> OldestClaimEventAsync(CancellationToken ct = default);

    /// <summary>Removes the pending event <paramref name="eventId"/> once it is published; false when it is no longer there.</summary>
    Task<bool> CompleteClaimEventAsync(string tenantId, string claimId, string eventId, CancellationToken ct = default);

    /// <summary>Records a failed publish and the next due time of the pending event <paramref name="eventId"/>.</summary>
    Task<bool> RescheduleClaimEventAsync(
        string tenantId, string claimId, string eventId, int attempts, long dueAtMs, string? lastError, CancellationToken ct = default);

    /// <summary>Claims of the request's tenant with an unresolved accumulator clamp review (work queue).</summary>
    Task<IReadOnlyList<Claim>> GetAccumulatorClampReviewsAsync(int limit, CancellationToken ct = default);

    /// <summary>Marks the claim's accumulator clamp review resolved (request tenant); false when there is none.</summary>
    Task<bool> ResolveAccumulatorClampReviewAsync(string claimId, string resolvedBy, CancellationToken ct = default);

    /// <summary>
    /// Records an accumulator review (clamped or refused commit) on the claim
    /// in its own write, keyed on the commit id — not on the outbox slot, so
    /// whichever attempt learns the store's answer saves it, even after
    /// another attempt cleared the slot. A no-op when the claim already holds
    /// a review of this commit. False when the claim does not exist.
    /// </summary>
    Task<bool> RecordAccumulatorReviewAsync(
        string tenantId, string claimId, AccumulatorClampReview review, CancellationToken ct = default);

    /// <summary>
    /// Pended claims of the request tenant whose routing pend code is
    /// <paramref name="pendCode"/> and that carry no other pend reason (those
    /// are left for an examiner), least recently pended first
    /// (<see cref="PendDetails.PendedAt"/>). A bulk re-run that pends a claim
    /// again re-stamps it, so it moves to the back instead of refilling every
    /// batch (#1299).
    /// </summary>
    Task<IReadOnlyList<Claim>> FindPendedByPendCodeAsync(string pendCode, int limit, CancellationToken ct = default);

    /// <summary>
    /// Finalized claims of the request tenant (Approved / PartiallyPaid /
    /// Paid; not superseded) last updated in [<paramref name="from"/>,
    /// <paramref name="to"/>] with no pending accumulator commit: the
    /// candidates of the post-rollout accumulator sweep.
    /// </summary>
    Task<IReadOnlyList<Claim>> FindFinalizedForAccumulatorSweepAsync(
        DateTime from, DateTime to, int limit, CancellationToken ct = default);

    /// <summary>
    /// Deny backfill (docs/operations/CLAIM-DENY-STATUS-BACKFILL.md): up to
    /// <paramref name="limit"/> claims of the request's tenant that are
    /// <see cref="ClaimStatus.Denied"/> with no payment-cycle evidence
    /// (<see cref="ClaimRepository.IsLegacyUnremittedDenied"/>) — rows the
    /// pipeline denied before <see cref="ClaimStatus.Deny"/> existed.
    /// </summary>
    Task<IReadOnlyList<Claim>> FindLegacyUnremittedDeniedAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Deny backfill: moves one such claim to <see cref="ClaimStatus.Deny"/>
    /// (VersionState Adjudicated), only while it still matches
    /// <see cref="ClaimRepository.IsLegacyUnremittedDenied"/> (checked by the
    /// write itself). False when it no longer does (already moved, finalized
    /// by a remittance, gone): a rerun is a no-op.
    /// </summary>
    Task<bool> TryReopenLegacyDeniedAsync(string tenantId, string claimId, CancellationToken ct = default);

    /// <summary>
    /// Atomically applies <paramref name="desiredStatus"/> to the single claim
    /// row identified by <paramref name="claimId"/> (direct row id, not a
    /// version-chain lookup — mirrors <see cref="GetByIdAsync"/>), subject to
    /// <see cref="ClaimRepository.BlocksSynchronousWriteback"/>: never
    /// overwrites a claim that is already Pended or at a final disposition.
    /// Backing store enforces this with a conditional write evaluated at
    /// commit time (a Mongo filter-based compare-and-set) — not a
    /// read-then-decide check — so it holds under a
    /// true concurrent write, not just sequential ordering.
    ///
    /// <para>
    /// Used by <c>PUT /{id}/status</c> (operational, non-outcome statuses
    /// only — see <c>ClaimsController.UpdateClaimStatus</c>) so its status
    /// decision is race-safe even though the rest of the claim still
    /// persists through <see cref="UpdateAsync"/>'s full-document replace.
    /// Also updates <c>VersionState</c> to keep it consistent with the
    /// applied status (<see cref="ClaimRepository.MapStatusToVersionState"/>).
    /// </para>
    /// </summary>
    Task<StatusWriteResult> TryTransitionStatusAsync(
        string tenantId,
        string claimId,
        ClaimStatus desiredStatus,
        CancellationToken ct = default);

    /// <summary>
    /// Supersession projection bypass for capability 5.12. Patches
    /// <c>SupersededAt</c>, <c>SupersededByVersionId</c>, and
    /// <c>VersionState=Adjusted</c> on a single claim row identified by
    /// <paramref name="tenantId"/> + <paramref name="claimId"/> regardless
    /// of its current terminal state. Required because the
    /// <see cref="UpdateAsync"/> terminal-state guard would reject a
    /// Paid → Adjusted transition (Paid is terminal).
    ///
    /// <para>
    /// 6th instance of the projection-metadata bypass pattern (Provider
    /// 5.4.5 integrity, Provider 5.6 credentialing, Provider 5.7+
    /// panel-gating, BP 5.5 network tiers, Claims 5.5 adjudication
    /// projection). The <c>Status</c> field is not touched here — the
    /// predecessor stays Paid until the 5.12b ReversalRun explicitly
    /// transitions it to Voided via <see cref="MarkVoidedProjectionAsync"/>.
    /// </para>
    ///
    /// Returns true on success, false when no row matched the filter.
    /// </summary>
    Task<bool> MarkSupersededProjectionAsync(
        string tenantId,
        string claimId,
        string supersessorVersionId,
        DateTime supersededAt,
        string? actorId,
        CancellationToken ct = default);

    /// <summary>
    /// Void projection bypass for capability 5.12 — terminal transition
    /// from Paid/Adjusted → Voided. Patches <c>Status=Voided</c>,
    /// <c>VersionState=Voided</c>, and <c>LastUpdatedDate/By</c>.
    /// Bypasses the <see cref="UpdateAsync"/> terminal-state guard (the
    /// guard exists to force adjustments through the explicit-new-version
    /// path; this method IS that explicit path's terminal write).
    ///
    /// Wired in 5.12a so <see cref="Services.IClaimFinalizationService.VoidAsync"/>
    /// has a write path; actual invocation occurs in 5.12b's
    /// <c>ReversalRunService</c>.
    ///
    /// Returns true on success, false when no row matched the filter.
    /// </summary>
    /// <param name="pendingAccumulatorReversal">
    /// The terminal engine-accumulator reversal the void owes
    /// (<see cref="Claim.PendingAccumulatorReversal"/>), saved by the same
    /// write: a void never persists without it.
    /// </param>
    Task<bool> MarkVoidedProjectionAsync(
        string tenantId,
        string claimId,
        DateTime voidedAt,
        string? actorId,
        CancellationToken ct = default,
        AccumulatorOutboxItem? pendingAccumulatorReversal = null,
        ClaimEventOutboxItem? claimEvent = null);

    /// <summary>
    /// Takes the examiner-resolution lock on a reprocessable claim (PR #1278
    /// round 3, L10): a conditional write that succeeds only while the claim
    /// is <see cref="ClaimStatus.Pended"/> or adjudicated but not finalized
    /// (<see cref="ClaimStatus.Approved"/> "Pay" / <see cref="ClaimStatus.Deny"/>,
    /// <see cref="ClaimStatusSemantics.IsReprocessable"/>) and holds no
    /// unexpired lock, so two concurrent resolutions cannot both re-adjudicate
    /// and publish. False when the claim is finalized (Paid / Denied / Voided)
    /// or not yet adjudicated, is locked by another resolution, or is gone.
    /// </summary>
    Task<bool> TryAcquireResolutionLockAsync(
        string tenantId, string claimId, string token, string? actorId, DateTime now, TimeSpan duration,
        CancellationToken ct = default);

    /// <summary>Clears the resolution lock if <paramref name="token"/> still holds it.</summary>
    Task ReleaseResolutionLockAsync(string tenantId, string claimId, string token, CancellationToken ct = default);

    /// <summary>
    /// True while the claim is still reprocessable (Pended / Pay / Deny) and <paramref name="token"/>
    /// holds its resolution lock, unexpired at <paramref name="now"/>. Read
    /// immediately before a side effect that is not itself fenced on the lock
    /// (a denial's accumulator reversal, PR #1278 follow-up 1).
    /// </summary>
    Task<bool> HoldsResolutionLockAsync(
        string tenantId, string claimId, string token, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Replaces the claim only while <paramref name="lockToken"/> still holds
    /// its resolution lock (fencing, round-3 verification L6): a resolver
    /// whose lock expired and was taken over cannot finalize. Returns the
    /// written claim, or null when the lock is no longer held.
    /// </summary>
    Task<Claim?> UpdateHoldingResolutionLockAsync(Claim claim, string lockToken, CancellationToken ct = default);

    /// <summary>
    /// Candidate prior claims for duplicate detection
    /// (<see cref="Services.Adjudication.Stages.DuplicateClaimStage"/>).
    /// Returns live versions for <paramref name="tenantId"/> +
    /// <paramref name="memberId"/> whose service period overlaps
    /// [<paramref name="serviceDateFrom"/>, <paramref name="serviceDateTo"/>],
    /// excluding every row of the version chain
    /// <paramref name="excludeClaimVersionId"/> (the claim being adjudicated
    /// and its predecessors/successors).
    ///
    /// <para>
    /// "Live" excludes Draft, Denied, Voided and superseded
    /// (<c>Adjusted</c> / <c>SupersededAt</c> set) versions and void
    /// requests (frequency code 8) — none of those represent a service the
    /// payer is still on the hook for. Filter shape is
    /// tenant + member equality with a service-date range so it rides the
    /// <c>(TenantId, MemberId, ServiceDateFrom)</c> Mongo index. Capped at
    /// <see cref="ClaimRepository.MaxDuplicateCandidates"/> rows.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Claim>> FindDuplicateCandidatesAsync(
        string tenantId,
        string memberId,
        DateTime serviceDateFrom,
        DateTime serviceDateTo,
        string excludeClaimVersionId,
        CancellationToken ct = default);
}
