using System.Text.Json;
using Microsoft.Azure.Cosmos;
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
    /// write path (the Argo workflow's synchronous finalize step, or an
    /// examiner override) already finalized the claim. Re-pending an
    /// already-<c>Pended</c> claim (a re-adjudication run refreshing
    /// <paramref name="pendDetails"/>) is allowed — Pended is not a final
    /// disposition. When <paramref name="resolvedStatus"/> is supplied, this
    /// call also projects async Pass/Deny/Reject outcomes through the same
    /// guarded status transition used by <c>UpdateAdjudicationSummaryAsync</c>,
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

    /// <summary>Claims of the request's tenant with an unresolved accumulator clamp review (work queue).</summary>
    Task<IReadOnlyList<Claim>> GetAccumulatorClampReviewsAsync(int limit, CancellationToken ct = default);

    /// <summary>Marks the claim's accumulator clamp review resolved (request tenant); false when there is none.</summary>
    Task<bool> ResolveAccumulatorClampReviewAsync(string claimId, string resolvedBy, CancellationToken ct = default);

    /// <summary>
    /// Fast claim-level adjudication projection for direct local workflow
    /// validation. Patches summary adjudication fields on a known claim row and
    /// intentionally skips full-claim hydration, line projection, and event
    /// emission. Use the full adjudication pipeline when downstream finalized
    /// events or line adjudications are required.
    ///
    /// <para>
    /// Residual-race fix — <paramref name="status"/> is applied through the
    /// same precedence guard as <see cref="TryTransitionStatusAsync"/>
    /// (<see cref="ClaimRepository.BlocksSynchronousWriteback"/>): it is never
    /// written over a claim already Pended or at a final disposition, because
    /// this method's only caller (the validator's synchronous write-back) is
    /// racing the async orchestrator's own Pend projection, not resolving one.
    /// <see cref="AdjudicationResult"/>/dates persist unconditionally either
    /// way — only the status transition is guarded — so a suppressed run never
    /// drops financial/audit data. See docs/architecture/
    /// claim-adjudication-pipeline.md D9b.
    /// </para>
    /// </summary>
    Task<StatusWriteResult> UpdateAdjudicationSummaryAsync(
        string tenantId,
        string claimId,
        AdjudicationResult adjudicationResult,
        ClaimStatus status,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically applies <paramref name="desiredStatus"/> to the single claim
    /// row identified by <paramref name="claimId"/> (direct row id, not a
    /// version-chain lookup — mirrors <see cref="GetByIdAsync"/>), subject to
    /// <see cref="ClaimRepository.BlocksSynchronousWriteback"/>: never
    /// overwrites a claim that is already Pended or at a final disposition.
    /// Backing store enforces this with a conditional write evaluated at
    /// commit time (Cosmos patch <c>FilterPredicate</c> / Mongo filter-based
    /// compare-and-set) — not a read-then-decide check — so it holds under a
    /// true concurrent write, not just sequential ordering.
    ///
    /// <para>
    /// Used by the two Argo-invoked synchronous write-back endpoints
    /// (<c>PUT /{id}/adjudication</c>, <c>PUT /{id}/status</c>) so their
    /// status decision is race-safe even though the rest of the claim still
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
    Task<bool> MarkVoidedProjectionAsync(
        string tenantId,
        string claimId,
        DateTime voidedAt,
        string? actorId,
        CancellationToken ct = default);

    /// <summary>
    /// Takes the examiner-resolution lock on a Pended claim (PR #1278 round
    /// 3, L10): a conditional write that succeeds only while the claim is
    /// <see cref="ClaimStatus.Pended"/> and holds no unexpired lock, so two
    /// concurrent approvals cannot both re-adjudicate and publish. False when
    /// the claim is not Pended, is locked by another resolution, or is gone.
    /// </summary>
    Task<bool> TryAcquireResolutionLockAsync(
        string tenantId, string claimId, string token, string? actorId, DateTime now, TimeSpan duration,
        CancellationToken ct = default);

    /// <summary>Clears the resolution lock if <paramref name="token"/> still holds it.</summary>
    Task ReleaseResolutionLockAsync(string tenantId, string claimId, string token, CancellationToken ct = default);

    /// <summary>
    /// True while the claim is still Pended and <paramref name="token"/>
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
    /// <c>(TenantId, MemberId, ServiceDateFrom)</c> Mongo index and stays
    /// single-partition on Cosmos. Capped at
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

public class ClaimRepository : IClaimRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ClaimRepository> _logger;
    private readonly IAdjudicationTenantContext? _adjudicationTenantContext;

    public ClaimRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ClaimRepository> logger,
        IAdjudicationTenantContext? adjudicationTenantContext = null)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Claims";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _adjudicationTenantContext = adjudicationTenantContext;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString()
            ?? _adjudicationTenantContext?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request or adjudication context");
        }
        return tenantId;
    }

    /// <summary>
    /// Hydrates legacy claim documents (predating versioning fields) with
    /// sensible defaults. Idempotent — running on a fully-versioned row
    /// is a no-op. Mirrors <c>ProviderRepository.Hydrate</c>.
    ///
    /// Internal (not public) so the 5.1b Cosmos partition migration can
    /// canonicalize rows during the copy from the legacy <c>Claims</c>
    /// container into the canonical <c>ClaimsV2</c> container — the new
    /// container then starts fully hydrated and downstream readers don't
    /// need to re-Hydrate post-migration. Same-assembly consumers
    /// (<c>ClaimMigrationService</c>) call directly; tests reach via the
    /// existing <c>InternalsVisibleTo</c> attribute on
    /// <c>claims-service.csproj</c>.
    /// </summary>
    internal static Claim Hydrate(Claim claim)
    {
        if (string.IsNullOrEmpty(claim.ClaimVersionId))
        {
            claim.ClaimVersionId = claim.Id;
        }
        if (claim.VersionNumber == 0)
        {
            claim.VersionNumber = 1;
        }
        if (claim.VersionState == ClaimVersionState.Unknown)
        {
            claim.VersionState = MapStatusToVersionState(claim.Status);
        }
        NormalizeAdjudicationProjection(claim);
        return claim;
    }

    internal static Claim NormalizeAdjudicationProjection(Claim claim)
    {
        if (!ShouldNormalizeFinancialSummary(claim.Status)
            || claim.ClaimLines.Count == 0)
        {
            return claim;
        }

        var lineAllowed = claim.ClaimLines.Sum(l => l.AdjudicationResult?.AllowedAmount ?? 0m);
        var linePaid = claim.ClaimLines.Sum(l => l.AdjudicationResult?.PaidAmount ?? 0m);
        var linePatientResponsibility = claim.ClaimLines.Sum(l => l.AdjudicationResult?.PatientResponsibility ?? 0m);

        if (lineAllowed == 0m && linePaid == 0m && linePatientResponsibility == 0m)
        {
            return claim;
        }

        claim.AdjudicationResult ??= new AdjudicationResult();
        if (HasEmptyFinancialSummary(claim.AdjudicationResult))
        {
            claim.AdjudicationResult.AllowedAmount = lineAllowed;
            claim.AdjudicationResult.PayerPayment = linePaid;
            claim.AdjudicationResult.PatientResponsibility = linePatientResponsibility;
            // Roll up the OOP-eligible amount only when every line carries
            // it; otherwise leave null so readers fall back to patient
            // responsibility.
            claim.AdjudicationResult.OopAppliedAmount =
                claim.ClaimLines.All(l => l.AdjudicationResult?.OopAppliedAmount is not null)
                    ? claim.ClaimLines.Sum(l => l.AdjudicationResult!.OopAppliedAmount!.Value)
                    : null;
        }

        claim.AdjudicationResult.DenialReasonCode = null;
        claim.AdjudicationResult.DenialReason = null;
        return claim;
    }

    private static bool ShouldNormalizeFinancialSummary(ClaimStatus status) => status switch
    {
        ClaimStatus.Approved or ClaimStatus.Paid or ClaimStatus.PartiallyPaid => true,
        _ => false
    };

    private static bool HasEmptyFinancialSummary(AdjudicationResult result) =>
        result.AllowedAmount == 0m
        && result.PayerPayment == 0m
        && result.PatientResponsibility == 0m
        && result.DeductibleAmount == 0m
        && result.CoinsuranceAmount == 0m
        && result.CopayAmount == 0m;

    /// <summary>
    /// Maps the legacy <see cref="ClaimStatus"/> operational signal onto a
    /// versioning <see cref="ClaimVersionState"/>. Public so the Mongo repo
    /// and tests share one canonical mapping; the table is documented in
    /// docs/architecture/claim-versioning.md.
    /// </summary>
    public static ClaimVersionState MapStatusToVersionState(ClaimStatus status) => status switch
    {
        ClaimStatus.Submitted or
        ClaimStatus.Received or
        ClaimStatus.InAdjudication or
        ClaimStatus.Pended => ClaimVersionState.Submitted,
        ClaimStatus.Approved => ClaimVersionState.Adjudicated,
        ClaimStatus.Paid or
        ClaimStatus.PartiallyPaid => ClaimVersionState.Paid,
        ClaimStatus.Denied => ClaimVersionState.Denied,
        ClaimStatus.Voided => ClaimVersionState.Voided,
        _ => ClaimVersionState.Submitted
    };

    /// <summary>
    /// Claim dispositions that <see cref="UpdateAdjudicationProjectionAsync"/>'s
    /// <c>isPend: true</c> path must never downgrade back to
    /// <see cref="ClaimStatus.Pended"/> — see that method's doc comment for
    /// the full precedence rule. Public so <see cref="ClaimRepositoryMongo"/>
    /// shares one canonical rule, mirroring <see cref="MapStatusToVersionState"/>.
    /// Pended is deliberately excluded: it is not a final disposition, so
    /// re-pending an already-Pended claim (a re-adjudication run refreshing
    /// PendDetails) is allowed.
    /// </summary>
    public static bool IsFinalDisposition(ClaimStatus status) => status switch
    {
        ClaimStatus.Approved or
        ClaimStatus.Denied or
        ClaimStatus.Paid or
        ClaimStatus.PartiallyPaid or
        ClaimStatus.Voided => true,
        _ => false
    };

    /// <summary>
    /// Every status <see cref="IsFinalDisposition"/> returns true for. Derived,
    /// not hand-duplicated, so the Cosmos <c>FilterPredicate</c> string built
    /// from it (<see cref="FinalDispositionFilterPredicate"/>) and Mongo's
    /// equivalent <c>$nin</c> filter can never drift from the canonical rule.
    /// </summary>
    public static readonly IReadOnlyList<ClaimStatus> FinalDispositions =
        Enum.GetValues<ClaimStatus>().Where(IsFinalDisposition).ToArray();

    /// <summary>
    /// True when <paramref name="status"/> must not be overwritten by a
    /// synchronous, non-authoritative adjudication write-back —
    /// <c>UpdateAdjudicationSummaryAsync</c> (the validator's own write-back)
    /// and <c>TryTransitionStatusAsync</c> (backing the Argo-invoked
    /// <c>PUT /{id}/adjudication</c> and <c>PUT /{id}/status</c> endpoints).
    ///
    /// <para>
    /// Composes <see cref="IsFinalDisposition"/> (a stray re-adjudication
    /// write-back must not re-litigate an already-completed disposition) with
    /// <see cref="ClaimStatus.Pended"/> (a human-review gate — only an
    /// explicit, deliberate action, <c>POST work-queue/{id}/override</c>,
    /// resolves it; never a synchronous write-back that doesn't know the pend
    /// happened). This is deliberately a DIFFERENT set than
    /// <see cref="IsFinalDisposition"/> alone: that predicate excludes Pended
    /// on purpose, for the opposite write direction (the async orchestrator's
    /// own Pend projection in <c>UpdateAdjudicationProjectionAsync</c>, which
    /// must still be allowed to re-pend an already-Pended claim). Two
    /// directions of one precedence lattice, both anchored here so no call
    /// site forks its own copy. See docs/architecture/
    /// claim-adjudication-pipeline.md D9b for the full writer x precedence
    /// table.
    /// </para>
    /// </summary>
    public static bool BlocksSynchronousWriteback(ClaimStatus status) =>
        status == ClaimStatus.Pended || IsFinalDisposition(status);

    /// <summary>Derived set backing <see cref="BlocksSynchronousWriteback"/> — see that method's doc comment.</summary>
    public static readonly IReadOnlyList<ClaimStatus> SynchronousWritebackBlockedStatuses =
        Enum.GetValues<ClaimStatus>().Where(BlocksSynchronousWriteback).ToArray();

    /// <summary>
    /// True for the narrow repair allowed by benchmark summary writeback: a
    /// row already says Denied, while the incoming summary is paid and carries
    /// no denial evidence. This prevents an impossible "Denied + payer payment
    /// + no denial reason" state from surviving while still protecting pends
    /// and non-paid summaries.
    /// </summary>
    public static bool CanRepairContradictoryDeniedSummary(
        ClaimStatus preWriteStatus,
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        preWriteStatus == ClaimStatus.Denied
        && desiredStatus == ClaimStatus.Approved
        && incomingAdjudication.PayerPayment > 0
        && !HasDenialEvidence(incomingAdjudication);

    /// <summary>
    /// True for the inverse narrow repair allowed by benchmark summary
    /// writeback: a row already says Approved, while the incoming summary is
    /// denied, pays nothing, and carries explicit denial evidence. This
    /// prevents an impossible "Approved + zero payer payment + denial reason"
    /// state without weakening pend protection or reopening a consistent
    /// final disposition.
    /// </summary>
    public static bool CanRepairContradictoryApprovedSummary(
        ClaimStatus preWriteStatus,
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        preWriteStatus == ClaimStatus.Approved
        && desiredStatus == ClaimStatus.Denied
        && incomingAdjudication.PayerPayment == 0
        && HasDenialEvidence(incomingAdjudication);

    /// <summary>
    /// True when an incoming adjudication result is strong enough to attempt
    /// the narrow live-row contradiction repair after a guarded status write
    /// was blocked. The live datastore predicate still decides whether a row
    /// is actually repairable, which lets this cover races where the status
    /// changed after the caller's pre-write snapshot.
    /// </summary>
    public static bool CanAttemptContradictoryStatusRepair(
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        (desiredStatus == ClaimStatus.Approved
            && incomingAdjudication.PayerPayment > 0
            && !HasDenialEvidence(incomingAdjudication))
        || (desiredStatus == ClaimStatus.Denied
            && incomingAdjudication.PayerPayment == 0
            && HasDenialEvidence(incomingAdjudication));

    private static bool HasDenialEvidence(AdjudicationResult? adjudication) =>
        adjudication is not null
        && (!string.IsNullOrWhiteSpace(adjudication.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(adjudication.DenialReason)
            || adjudication.AdjustmentReasons.Any());

    private static bool IsTerminal(ClaimVersionState state) => state switch
    {
        ClaimVersionState.Paid or
        ClaimVersionState.Denied or
        ClaimVersionState.Voided or
        ClaimVersionState.Adjusted => true,
        _ => false
    };

    public async Task<Claim?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Claim>(
                id,
                new PartitionKey(tenantId));

            // 5.1b: with /tenantId partition, a cross-tenant lookup throws
            // CosmosException 404 (caught below) rather than returning a
            // foreign-tenant document. The in-memory tenant equality check
            // is intentionally retained as defense in depth: it makes the
            // tenant-isolation contract explicit at the read point and
            // catches any future code path that might bypass the
            // partition-keyed read (e.g. a cross-partition query that
            // hydrates this method's return shape). Cheap, defensive,
            // intentionally NOT dead code.
            if (response.Resource.TenantId != tenantId)
            {
                return null;
            }

            return Hydrate(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Claim?> GetByClaimNumberAsync(string claimNumber)
    {
        var tenantId = GetTenantId();

        // TOP 1 + ORDER BY versionNumber DESC keeps RU cost bounded — only
        // the head version is needed; pulling every version-row for the
        // claim into memory just to take the first wastes RUs at scale.
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId AND c.claimNumber = @claimNumber " +
            "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimNumber", claimNumber);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var head = page.FirstOrDefault();
        return head != null ? Hydrate(head) : null;
    }

    public async Task<IEnumerable<Claim>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var (claims, _) = await SearchWithCountAsync(
            memberId,
            providerNPI,
            serviceDateFrom,
            serviceDateTo,
            status,
            lineOfBusiness,
            page,
            pageSize);

        return claims;
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchWithCountAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { { "@tenantId", tenantId } };

        if (!string.IsNullOrEmpty(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            conditions.Add("(c.billingProviderNPI = @providerNPI OR c.renderingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }

        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }

        if (status.HasValue)
        {
            conditions.Add(EnumInClause("c.status", "status", parameters, status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            conditions.Add(EnumInClause("c.lineOfBusiness", "lineOfBusiness", parameters, lineOfBusiness.Value));
        }

        var totalCount = await CountClaimsAsync(conditions, parameters, tenantId);
        var safePage = Math.Max(page, 1);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.submittedDate DESC
            OFFSET {(safePage - 1) * safePageSize} LIMIT {safePageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
        {
            queryDef.WithParameter(key, value);
        }

        var iterator = _container.GetItemQueryIterator<Claim>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Claim>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return (results.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchByIdsAsync(
        IReadOnlyCollection<string> claimIds,
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        int page,
        int pageSize)
    {
        if (claimIds.Count == 0)
        {
            return (Array.Empty<Claim>(), 0);
        }

        var tenantId = GetTenantId();
        var distinctClaimIds = claimIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctClaimIds.Length == 0)
        {
            return (Array.Empty<Claim>(), 0);
        }

        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            "ARRAY_CONTAINS(@claimIds, c.id)"
        };
        var parameters = new Dictionary<string, object>
        {
            ["@tenantId"] = tenantId,
            ["@claimIds"] = distinctClaimIds
        };

        if (!string.IsNullOrWhiteSpace(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }
        if (!string.IsNullOrWhiteSpace(providerNPI))
        {
            conditions.Add("(c.billingProviderNPI = @providerNPI OR c.renderingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }
        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }
        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }
        if (status.HasValue)
        {
            conditions.Add(EnumInClause("c.status", "status", parameters, status.Value));
        }

        var totalCount = await CountClaimsAsync(conditions, parameters, tenantId);
        var safePage = Math.Max(page, 1);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.submittedDate DESC
            OFFSET {(safePage - 1) * safePageSize} LIMIT {safePageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
        {
            queryDef.WithParameter(key, value);
        }

        var iterator = _container.GetItemQueryIterator<Claim>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Claim>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return (results.Select(Hydrate).ToList(), totalCount);
    }

    private async Task<int> CountClaimsAsync(
        IReadOnlyCollection<string> conditions,
        IReadOnlyDictionary<string, object> parameters,
        string tenantId)
    {
        var queryDef = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {string.Join(" AND ", conditions)}");
        foreach (var (key, value) in parameters)
        {
            queryDef.WithParameter(key, value);
        }

        var iterator = _container.GetItemQueryIterator<int>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var count = 0;
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            count += response.FirstOrDefault();
        }

        return count;
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchForMemberAsync(
        string memberId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        string? providerNPI,
        ClaimType? claimType,
        decimal? amountMin,
        decimal? amountMax,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            "c.memberId = @memberId"
        };
        var parameters = new Dictionary<string, object>
        {
            ["@tenantId"] = tenantId,
            ["@memberId"] = memberId
        };

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }
        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }
        if (status.HasValue)
        {
            conditions.Add(EnumInClause("c.status", "status", parameters, status.Value));
        }
        if (!string.IsNullOrEmpty(providerNPI))
        {
            conditions.Add("(c.billingProviderNPI = @providerNPI OR c.renderingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }
        if (claimType.HasValue)
        {
            conditions.Add(EnumInClause("c.claimType", "claimType", parameters, claimType.Value));
        }
        if (amountMin.HasValue)
        {
            conditions.Add("c.totalChargeAmount >= @amountMin");
            parameters["@amountMin"] = amountMin.Value;
        }
        if (amountMax.HasValue)
        {
            conditions.Add("c.totalChargeAmount <= @amountMax");
            parameters["@amountMax"] = amountMax.Value;
        }

        var where = string.Join(" AND ", conditions);

        var countQuery = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {where}");
        foreach (var (k, v) in parameters) countQuery.WithParameter(k, v);

        var totalCount = 0;
        var partitionRequestOptions = new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) };
        var countIterator = _container.GetItemQueryIterator<int>(countQuery, requestOptions: partitionRequestOptions);
        while (countIterator.HasMoreResults)
        {
            var response = await countIterator.ReadNextAsync();
            totalCount += response.FirstOrDefault();
        }

        var pageQuery = new QueryDefinition(
            $"SELECT * FROM c WHERE {where} " +
            $"ORDER BY c.submittedDate DESC " +
            $"OFFSET {(page - 1) * pageSize} LIMIT {pageSize}");
        foreach (var (k, v) in parameters) pageQuery.WithParameter(k, v);

        var items = new List<Claim>();
        var iterator = _container.GetItemQueryIterator<Claim>(pageQuery, requestOptions: partitionRequestOptions);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            items.AddRange(response);
        }

        return (items.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<ClaimsSummary> GetClaimsSummaryAsync(
        DateTime from,
        DateTime to,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();

        var lobParams = new Dictionary<string, object>();
        var lobCondition = lineOfBusiness.HasValue
            ? "AND " + EnumInClause("c.lineOfBusiness", "lineOfBusiness", lobParams, lineOfBusiness.Value)
            : "";

        var queryText = $@"
            SELECT
                COUNT(1) as TotalClaims,
                SUM(CASE WHEN {EnumInLiteral("c.status", ClaimStatus.Approved)} THEN 1 ELSE 0 END) as ApprovedClaims,
                SUM(CASE WHEN {EnumInLiteral("c.status", ClaimStatus.Denied)} THEN 1 ELSE 0 END) as DeniedClaims,
                SUM(CASE WHEN {EnumInLiteral("c.status", ClaimStatus.Pended)} THEN 1 ELSE 0 END) as PendedClaims,
                SUM(CASE WHEN {EnumInLiteral("c.status", ClaimStatus.Paid)} THEN 1 ELSE 0 END) as PaidClaims,
                SUM(c.totalChargeAmount) as TotalChargeAmount,
                SUM(c.adjudicationResult.allowedAmount ?? 0) as TotalAllowedAmount,
                SUM(c.adjudicationResult.payerPayment ?? 0) as TotalPaidAmount
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            {lobCondition}";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        foreach (var (name, value) in lobParams) queryDef = queryDef.WithParameter(name, value);

        var iterator = _container.GetItemQueryIterator<dynamic>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var summary = new ClaimsSummary();

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var result = response.FirstOrDefault();

            if (result != null)
            {
                summary.TotalClaims = result.TotalClaims ?? 0;
                summary.ApprovedClaims = result.ApprovedClaims ?? 0;
                summary.DeniedClaims = result.DeniedClaims ?? 0;
                summary.PendedClaims = result.PendedClaims ?? 0;
                summary.PaidClaims = result.PaidClaims ?? 0;
                summary.TotalChargeAmount = result.TotalChargeAmount ?? 0;
                summary.TotalAllowedAmount = result.TotalAllowedAmount ?? 0;
                summary.TotalPaidAmount = result.TotalPaidAmount ?? 0;

                // Calculate approval rate
                if (summary.TotalClaims > 0)
                {
                    summary.ApprovalRate = (decimal)summary.ApprovedClaims / summary.TotalClaims * 100;
                }
            }
        }

        // Calculate average processing days (separate query for adjudicated claims)
        var processingQueryText = $@"
            SELECT AVG(
                DateTimeDiff('day', c.submittedDate, c.adjudicatedDate)
            ) as AvgDays
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND c.adjudicatedDate != null
            {lobCondition}";

        var processingQueryDef = new QueryDefinition(processingQueryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        foreach (var (name, value) in lobParams) processingQueryDef = processingQueryDef.WithParameter(name, value);

        var processingIterator = _container.GetItemQueryIterator<dynamic>(
            processingQueryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (processingIterator.HasMoreResults)
        {
            var response = await processingIterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            summary.AverageProcessingDays = result?.AvgDays ?? 0;
        }

        return summary;
    }

    public async Task<Claim> CreateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;

        if (string.IsNullOrEmpty(claim.Id))
        {
            claim.Id = Guid.NewGuid().ToString();
        }

        // Initialize the version chain if the caller hasn't done so. New claims
        // start at VersionState=Submitted (matching the existing default
        // ClaimStatus.Submitted on uninitialized rows). Capability 5.3
        // (Submission API) ratified this Submitted-on-create behavior — there
        // is no Draft state in the canonical payer submission flow; Draft is
        // reserved for the future adjustment workflow (capability 5.12).
        if (string.IsNullOrEmpty(claim.ClaimVersionId))
        {
            claim.ClaimVersionId = claim.Id;
        }
        if (claim.VersionNumber == 0)
        {
            claim.VersionNumber = 1;
        }
        if (claim.VersionState == ClaimVersionState.Unknown)
        {
            claim.VersionState = MapStatusToVersionState(claim.Status);
        }

        var response = await _container.CreateItemAsync(claim, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Claim> UpdateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;

        // Reject mutations on terminal versions. Adjustments must go through
        // the explicit "create new version with PredecessorVersionId" path
        // (capability 5.12). This guard mirrors ProviderRepository.UpdateAsync.
        Claim? existing;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(claim.Id, new PartitionKey(tenantId));
            existing = read.Resource.TenantId == tenantId ? Hydrate(read.Resource) : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing == null)
        {
            // Surface a domain-specific not-found rather than letting the
            // ReplaceItemAsync 404 bubble through ExceptionHandlingMiddleware
            // as a 500. Controllers map IsNotFound to HTTP 404.
            throw new ClaimVersionStateException(
                claim.ClaimVersionId, claim.Id, claim.VersionState,
                $"Claim version {claim.Id} not found for update.")
            {
                IsNotFound = true
            };
        }

        if (IsTerminal(existing.VersionState))
        {
            throw new ClaimVersionStateException(
                existing.ClaimVersionId, existing.Id, existing.VersionState,
                $"Claim version {existing.Id} is in terminal state {existing.VersionState} and cannot be updated. " +
                "Create an adjustment version via the adjustment workflow.");
        }

        try
        {
            var response = await _container.ReplaceItemAsync(
                claim,
                claim.Id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Race: the row was deleted between the pre-read above and the
            // replace. Same domain-specific not-found surface.
            throw new ClaimVersionStateException(
                claim.ClaimVersionId, claim.Id, claim.VersionState,
                $"Claim version {claim.Id} not found for update (deleted concurrently).")
            {
                IsNotFound = true
            };
        }
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Claim>(id, new PartitionKey(tenantId));
    }

    // ── Versioning surface (5.1) ─────────────────────────────────────────

    public async Task<Claim?> GetLatestVersionAsync(string claimVersionId, DateTime asOf)
    {
        var tenantId = GetTenantId();

        // "In effect at asOf" means PublishedAt <= asOf AND
        // (SupersededAt is null OR SupersededAt > asOf). For legacy rows
        // missing PublishedAt/SupersededAt/versionState, the (NOT IS_DEFINED
        // OR null) clauses keep them visible — hydration on read maps
        // Status onto VersionState so the caller sees the legacy row as the
        // head. The versionState predicate uses (NOT IS_DEFINED OR ...)
        // because Cosmos SQL evaluates undefined-vs-anything as undefined
        // (≠ true), which would silently drop legacy rows.
        //
        // The Cosmos serializer persists enums camelCase ("draft"), so the
        // Draft exclusion binds the persisted spelling (plus PascalCase for
        // rows from older writers) — comparing against "Draft" alone let a
        // draft row through as the head.
        var draftParams = new Dictionary<string, object>();
        var draftClause = EnumInClause("c.versionState", "draft", draftParams, ClaimVersionState.Draft);
        var query = new QueryDefinition($@"
            SELECT TOP 1 *
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
              AND (NOT IS_DEFINED(c.versionState) OR c.versionState = null OR NOT ({draftClause}))
              AND (NOT IS_DEFINED(c.publishedAt) OR c.publishedAt = null OR c.publishedAt <= @asOf)
              AND (NOT IS_DEFINED(c.supersededAt) OR c.supersededAt = null OR c.supersededAt > @asOf)
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId)
            .WithParameter("@asOf", asOf);
        foreach (var (name, value) in draftParams) query = query.WithParameter(name, value);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var head = page.FirstOrDefault();
        return head != null ? Hydrate(head) : null;
    }

    public async Task<Claim?> GetVersionAsync(string claimVersionId, string versionId)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT TOP 1 *
            FROM c
            WHERE c.tenantId = @tenantId
              AND c.id = @versionId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@versionId", versionId)
            .WithParameter("@claimVersionId", claimVersionId);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var match = page.FirstOrDefault();
        return match != null ? Hydrate(match) : null;
    }

    public async Task<(IReadOnlyList<Claim> Items, string? ContinuationToken)> ListVersionsAsync(
        string claimVersionId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT *
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                MaxItemCount = pageSize,
                PartitionKey = new PartitionKey(tenantId),
            });

        if (!iterator.HasMoreResults)
        {
            return (Array.Empty<Claim>(), null);
        }

        var page = await iterator.ReadNextAsync();
        var items = page.Select(Hydrate).ToList();
        return (items, page.ContinuationToken);
    }

    public async Task<bool> UpdateAdjudicationProjectionAsync(
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
        AccumulatorOutboxItem? pendingAccumulatorCommit = null)
    {
        // Resolve the head (non-terminal-but-adjudicatable) row by chain key.
        // PatchItemAsync is keyed on the per-row document Id, so we look up
        // the row id first. We accept any version that isn't Draft or
        // Voided — adjudication runs against Submitted / Adjudicated rows
        // (re-adjudication is allowed).
        // versionState literals bind the persisted camelCase spelling (plus
        // PascalCase for older rows); "Submitted"/"Adjudicated" alone never
        // matched a versioned row, so only legacy rows could be patched.
        var stateParams = new Dictionary<string, object>();
        var stateClause = EnumInClause("c.versionState", "adjudicatableState", stateParams,
            ClaimVersionState.Submitted, ClaimVersionState.Adjudicated, ClaimVersionState.Unknown);
        var query = new QueryDefinition($@"
            SELECT TOP 1 c.id
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
              AND (NOT IS_DEFINED(c.versionState) OR {stateClause})
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId);
        foreach (var (name, value) in stateParams) query = query.WithParameter(name, value);

        string? rowId = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            rowId = page.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(rowId)) return false;

        // Apply the line adjudication results to the head row's claim lines
        // by line number. We can't patch nested array elements positionally
        // in Cosmos without knowing indexes, so we read-modify-write the
        // ClaimLines array. AdjudicationResult itself is a flat patch.
        Claim? head;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(rowId, new PartitionKey(tenantId), cancellationToken: ct);
            head = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        // The query above accepts rows with NOT IS_DEFINED(c.versionState)
        // so legacy claims (no version fields) can still receive
        // adjudication writes. But a legacy row with Status=Paid hydrates
        // to VersionState=Paid — which is terminal and must NOT be patched.
        // Hydrate then enforce the terminal-state guard before mutating.
        head = Hydrate(head);
        if (IsTerminal(head.VersionState)) return false;

        // The adjudication orchestrator (5.5) emits one LineAdjudicationResult
        // per ClaimLine in claim-line order; counts that disagree are a 5.5
        // input-validation issue, not a 5.1 bypass concern. Apply when shapes
        // match; otherwise leave existing line results untouched.
        if (head.ClaimLines.Count == lineResults.Count)
        {
            for (var i = 0; i < head.ClaimLines.Count; i++)
            {
                head.ClaimLines[i].AdjudicationResult = lineResults[i];
            }
        }

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/adjudicationResult", adjudicationResult),
            PatchOperation.Set("/claimLines", head.ClaimLines),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
        };

        // 5.7 — project deterministic pend reason when the adjudication
        // pipeline (NCCI / MUE / future authorization stages) populated
        // it. Null leaves the head row's existing PendDetails untouched
        // so a clean re-adjudication doesn't drop a prior pend reason
        // unless the new pipeline run replaces it. To explicitly clear
        // a stale pend, the orchestrator can pass an empty PendDetails
        // — but the v1 stage path doesn't do that today.
        if (pendDetails is not null)
        {
            ops.Add(PatchOperation.Set("/pendDetails", pendDetails));
        }

        // The orchestrator resolves BenefitPlanId in-memory from the
        // member's active coverage for X12 837 claims that arrive without
        // one (see ClaimAdjudicationOrchestrator), but that in-memory claim
        // is never otherwise persisted — this bypass write is the only path
        // back to the row. Only patch when the row doesn't already carry a
        // BenefitPlanId, since a claim that already had one skips the
        // orchestrator's resolution step entirely and this stays null.
        if (!string.IsNullOrWhiteSpace(resolvedBenefitPlanId) && string.IsNullOrWhiteSpace(head.BenefitPlanId))
        {
            ops.Add(PatchOperation.Set("/benefitPlanId", resolvedBenefitPlanId));
        }

        // Follow-up 1: an examiner-approval re-run writes only while its
        // resolution lock is still held, evaluated by Cosmos at commit time.
        PatchItemRequestOptions? fence = null;
        if (requiredResolutionLockToken is not null)
        {
            fence = new PatchItemRequestOptions
            {
                FilterPredicate = $"FROM c WHERE c.resolutionLock.token = '{ResolutionLockTokenLiteral(requiredResolutionLockToken)}'",
            };
        }

        try
        {
            await _container.PatchItemAsync<Claim>(
                rowId,
                new PartitionKey(tenantId),
                ops,
                fence,
                ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Row deleted between lookup and patch.
            return false;
        }
        catch (CosmosException ex) when (fence is not null && ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // The resolution lock was taken over: nothing written.
            return false;
        }

        if (isPend)
        {
            // Defect A fix, made atomic — project the orchestrator's Pend outcome
            // onto ClaimStatus in a SEPARATE conditional patch, evaluated against
            // the row's live state at commit time via FilterPredicate rather than
            // the `head` snapshot read above. Precedence: never downgrade a claim
            // that already reached a later-stage disposition (IsFinalDisposition)
            // — e.g. because the Argo workflow's synchronous finalize step, or an
            // examiner override, raced ahead of this async projection. A
            // read-then-decide check (the original form of this guard) only
            // catches that race when the other write happens to land before this
            // method's own read; a concurrent write landing in between read and
            // write would still get clobbered. The conditional patch closes that
            // window: Cosmos evaluates FilterPredicate against the document as it
            // exists at the moment this patch actually commits. Re-pending an
            // already-Pended claim is allowed — Pended is excluded from
            // FinalDispositions by design. A blocked patch is not an error; the
            // /adjudicationResult + /claimLines + /pendDetails write above still
            // succeeded, so this method still returns true either way.
            try
            {
                await _container.PatchItemAsync<Claim>(
                    rowId,
                    new PartitionKey(tenantId),
                    new List<PatchOperation> { PatchOperation.Set("/status", ClaimStatus.Pended) },
                    new PatchItemRequestOptions
                    {
                        FilterPredicate = WithResolutionLockFence(FinalDispositionFilterPredicate, requiredResolutionLockToken),
                    },
                    ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Guard correctly blocked the downgrade — not an error.
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Row deleted between the first patch and this one.
            }

            return true;
        }

        if (resolvedStatus is not null)
        {
            await TryPatchStatusAsync(
                    tenantId,
                    rowId,
                    resolvedStatus.Value,
                    MapStatusToVersionState(resolvedStatus.Value),
                    ct,
                    adjudicationResult,
                    head.Status,
                    requiredResolutionLockToken,
                    resolvedStatus == ClaimStatus.Approved ? pendingAccumulatorCommit : null)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<StatusWriteResult> UpdateAdjudicationSummaryAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        ClaimStatus status,
        CancellationToken ct = default)
    {
        var draftParams = new Dictionary<string, object>();
        var draftClause = EnumInClause("c.versionState", "draft", draftParams, ClaimVersionState.Draft);
        var query = new QueryDefinition($@"
            SELECT TOP 1 c.id, c.status
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
              AND (NOT IS_DEFINED(c.versionState) OR c.versionState = null OR NOT ({draftClause}))
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId);
        foreach (var (name, value) in draftParams) query = query.WithParameter(name, value);

        HeadIdResult? head = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            head = page.FirstOrDefault();
        }

        if (head is null || string.IsNullOrEmpty(head.Id)) return StatusWriteResult.NotFoundResult;

        // Residual-race fix — financial/audit data (AdjudicationResult, dates)
        // persists by chain/id even if async adjudication already finalized
        // the row. Only /status + /versionState are guarded, in a separate
        // conditional patch — see TryPatchStatusAsync.
        var now = DateTime.UtcNow;
        var summaryOps = new List<PatchOperation>
        {
            PatchOperation.Set("/adjudicationResult", adjudicationResult),
            PatchOperation.Set("/adjudicatedDate", now),
            PatchOperation.Set("/lastUpdatedDate", now),
        };

        try
        {
            await _container.PatchItemAsync<Claim>(
                head.Id,
                new PartitionKey(tenantId),
                summaryOps,
                cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return StatusWriteResult.NotFoundResult;
        }

        return await TryPatchStatusAsync(
            tenantId,
            head.Id,
            status,
            ClaimVersionState.Adjudicated,
            ct,
            adjudicationResult,
            head.Status);
    }

    public Task<StatusWriteResult> TryTransitionStatusAsync(
        string tenantId,
        string claimId,
        ClaimStatus desiredStatus,
        CancellationToken ct = default) =>
        TryPatchStatusAsync(tenantId, claimId, desiredStatus, MapStatusToVersionState(desiredStatus), ct);

    /// <summary>
    /// Shared atomic status write behind <see cref="UpdateAdjudicationSummaryAsync"/>
    /// and <see cref="TryTransitionStatusAsync"/>. Guarded by
    /// <see cref="BlocksSynchronousWriteback"/> via a Cosmos patch
    /// <c>FilterPredicate</c>, evaluated server-side against the row's live
    /// state at commit time — not a read-then-decide check, so it holds under
    /// a true concurrent write. On a blocked write, issues one fallback read
    /// to report the row's actual current status (needed to disambiguate
    /// "guard blocked it" from "row was deleted" — a rejected conditional
    /// patch doesn't return the document — and to tell the caller what
    /// actually persisted); this only happens on the (expected to be rare)
    /// suppressed path, not the hot path.
    /// </summary>
    private async Task<StatusWriteResult> TryPatchStatusAsync(
        string tenantId,
        string rowId,
        ClaimStatus desiredStatus,
        ClaimVersionState desiredVersionState,
        CancellationToken ct,
        AdjudicationResult? incomingAdjudication = null,
        ClaimStatus? preWriteStatus = null,
        string? requiredResolutionLockToken = null,
        AccumulatorOutboxItem? pendingAccumulatorCommit = null)
    {
        var statusOps = new List<PatchOperation>
        {
            PatchOperation.Set("/status", desiredStatus),
            PatchOperation.Set("/versionState", desiredVersionState),
        };
        // The accumulator outbox entry lands with the status that finalizes
        // the claim (same patch, same predicate), or not at all.
        if (pendingAccumulatorCommit is not null)
            statusOps.Add(PatchOperation.Set("/pendingAccumulatorCommit", pendingAccumulatorCommit));
        var options = new PatchItemRequestOptions
        {
            FilterPredicate = WithResolutionLockFence(SynchronousWritebackBlockedFilterPredicate, requiredResolutionLockToken),
        };

        try
        {
            await _container.PatchItemAsync<Claim>(rowId, new PartitionKey(tenantId), statusOps, options, ct);
            return new StatusWriteResult(StatusWriteOutcome.Applied, desiredStatus);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return StatusWriteResult.NotFoundResult;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            var snapshotAllowsRepair = incomingAdjudication is not null
                && preWriteStatus is not null
                && (CanRepairContradictoryDeniedSummary(
                        preWriteStatus.Value,
                        desiredStatus,
                        incomingAdjudication)
                    || CanRepairContradictoryApprovedSummary(
                        preWriteStatus.Value,
                        desiredStatus,
                        incomingAdjudication));
            var liveEvidenceAllowsRepair = incomingAdjudication is not null
                && CanAttemptContradictoryStatusRepair(desiredStatus, incomingAdjudication);

            if (snapshotAllowsRepair || liveEvidenceAllowsRepair)
            {
                var repairFilter = desiredStatus == ClaimStatus.Denied
                    ? ContradictoryApprovedRepairFilterPredicate
                    : ContradictoryDeniedRepairFilterPredicate;
                var repairOptions = new PatchItemRequestOptions
                {
                    FilterPredicate = WithResolutionLockFence(repairFilter, requiredResolutionLockToken),
                };
                try
                {
                    await _container.PatchItemAsync<Claim>(rowId, new PartitionKey(tenantId), statusOps, repairOptions, ct);
                    return new StatusWriteResult(StatusWriteOutcome.Applied, desiredStatus);
                }
                catch (CosmosException repairEx) when (repairEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return StatusWriteResult.NotFoundResult;
                }
                catch (CosmosException repairEx) when (repairEx.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                {
                    // Another writer changed the row after summary writeback.
                    // Fall through to the normal readback so callers see the
                    // persisted status that actually won.
                }
            }

            try
            {
                var read = await _container.ReadItemAsync<Claim>(rowId, new PartitionKey(tenantId), cancellationToken: ct);
                return new StatusWriteResult(StatusWriteOutcome.Suppressed, read.Resource.Status);
            }
            catch (CosmosException readEx) when (readEx.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Deleted between the blocked patch and this fallback read —
                // functionally not-found from the caller's perspective.
                return StatusWriteResult.NotFoundResult;
            }
        }
    }

    // ── accumulator outbox ──────────────────────────────────────────────

    private static string OutboxPath(AccumulatorOutboxKind kind) =>
        kind == AccumulatorOutboxKind.Commit ? "pendingAccumulatorCommit" : "pendingAccumulatorReversal";

    /// <summary>Outbox ids are generated (hex / "reversal-" + hex); anything else is refused before it reaches a predicate.</summary>
    private static string OutboxIdLiteral(string itemId) =>
        itemId.Length is > 0 and <= 80 && itemId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            ? itemId
            : throw new ArgumentException("Invalid accumulator outbox id.", nameof(itemId));

    public async Task<IReadOnlyList<Claim>> FindDueAccumulatorOutboxAsync(long nowMs, int limit, CancellationToken ct = default)
    {
        // Cross-partition on purpose: the dispatcher serves every tenant.
        var query = new QueryDefinition(@"
            SELECT TOP @limit * FROM c
            WHERE (IS_DEFINED(c.pendingAccumulatorCommit) AND NOT IS_NULL(c.pendingAccumulatorCommit)
                   AND c.pendingAccumulatorCommit.dueAtMs <= @now)
               OR (IS_DEFINED(c.pendingAccumulatorReversal) AND NOT IS_NULL(c.pendingAccumulatorReversal)
                   AND c.pendingAccumulatorReversal.dueAtMs <= @now)")
            .WithParameter("@limit", limit)
            .WithParameter("@now", nowMs);
        var claims = new List<Claim>();
        using var iterator = _container.GetItemQueryIterator<Claim>(query);
        while (iterator.HasMoreResults && claims.Count < limit)
            claims.AddRange((await iterator.ReadNextAsync(ct)).Select(Hydrate));
        return claims;
    }

    public async Task<long?> OldestAccumulatorOutboxAsync(CancellationToken ct = default)
    {
        long? oldest = null;
        foreach (var path in new[] { "pendingAccumulatorCommit", "pendingAccumulatorReversal" })
        {
            var query = new QueryDefinition($@"
                SELECT VALUE MIN(c.{path}.createdAtMs) FROM c
                WHERE IS_DEFINED(c.{path}) AND NOT IS_NULL(c.{path})");
            using var iterator = _container.GetItemQueryIterator<long?>(query);
            while (iterator.HasMoreResults)
            {
                foreach (var value in await iterator.ReadNextAsync(ct))
                {
                    if (value is { } v && (oldest is null || v < oldest)) oldest = v;
                }
            }
        }
        return oldest;
    }

    public async Task<Claim?> GetForAccumulatorOutboxAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        try
        {
            var read = await _container.ReadItemAsync<Claim>(claimId, new PartitionKey(tenantId), cancellationToken: ct);
            return Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> CompleteAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        AccumulatorClampReview? clampReview = null, CancellationToken ct = default)
    {
        var path = OutboxPath(kind);
        var ops = new List<PatchOperation> { PatchOperation.Set<AccumulatorOutboxItem?>("/" + path, null) };
        if (clampReview is not null) ops.Add(PatchOperation.Set("/accumulatorClampReview", clampReview));
        return await PatchOutboxAsync(tenantId, claimId, path, itemId, ops, ct);
    }

    public async Task<bool> RescheduleAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        int attempts, DateTime nextAttemptAt, string? lastError, CancellationToken ct = default)
    {
        var path = OutboxPath(kind);
        var ops = new List<PatchOperation>
        {
            PatchOperation.Set($"/{path}/attempts", attempts),
            PatchOperation.Set($"/{path}/nextAttemptAt", nextAttemptAt),
            PatchOperation.Set($"/{path}/dueAtMs", AccumulatorOutboxItem.ToMs(nextAttemptAt)),
            PatchOperation.Set($"/{path}/lastError", lastError),
        };
        return await PatchOutboxAsync(tenantId, claimId, path, itemId, ops, ct);
    }

    /// <summary>Patches the claim only while its outbox slot still holds <paramref name="itemId"/> (evaluated at commit).</summary>
    private async Task<bool> PatchOutboxAsync(
        string tenantId, string claimId, string path, string itemId, List<PatchOperation> ops, CancellationToken ct)
    {
        try
        {
            await _container.PatchItemAsync<Claim>(claimId, new PartitionKey(tenantId), ops,
                new PatchItemRequestOptions { FilterPredicate = $"FROM c WHERE c.{path}.id = '{OutboxIdLiteral(itemId)}'" }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<Claim>> GetAccumulatorClampReviewsAsync(int limit, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(@"
            SELECT TOP @limit * FROM c
            WHERE c.tenantId = @tenantId
              AND IS_DEFINED(c.accumulatorClampReview) AND NOT IS_NULL(c.accumulatorClampReview)
              AND c.accumulatorClampReview.resolved = false")
            .WithParameter("@limit", limit)
            .WithParameter("@tenantId", tenantId);
        var claims = new List<Claim>();
        using var iterator = _container.GetItemQueryIterator<Claim>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
            claims.AddRange((await iterator.ReadNextAsync(ct)).Select(Hydrate));
        return claims;
    }

    public async Task<bool> ResolveAccumulatorClampReviewAsync(string claimId, string resolvedBy, CancellationToken ct = default)
    {
        try
        {
            await _container.PatchItemAsync<Claim>(claimId, new PartitionKey(GetTenantId()),
                [
                    PatchOperation.Set("/accumulatorClampReview/resolved", true),
                    PatchOperation.Set("/accumulatorClampReview/resolvedBy", resolvedBy),
                    PatchOperation.Set("/accumulatorClampReview/resolvedAt", DateTime.UtcNow),
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE IS_DEFINED(c.accumulatorClampReview) AND NOT IS_NULL(c.accumulatorClampReview)",
                }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Cosmos patch FilterPredicate for <see cref="BlocksSynchronousWriteback"/> — built from <see cref="SynchronousWritebackBlockedStatuses"/> so it can't drift from the canonical rule.</summary>
    private static readonly string SynchronousWritebackBlockedFilterPredicate =
        BuildStatusNotInFilterPredicate(SynchronousWritebackBlockedStatuses);

    private static readonly string ContradictoryDeniedRepairFilterPredicate =
        $"FROM c WHERE c.status = '{CosmosStatusLiteral(ClaimStatus.Denied)}' " +
        "AND IS_DEFINED(c.adjudicationResult) " +
        "AND c.adjudicationResult.payerPayment > 0 " +
        "AND (NOT IS_DEFINED(c.adjudicationResult.denialReasonCode) " +
        "OR IS_NULL(c.adjudicationResult.denialReasonCode) " +
        "OR c.adjudicationResult.denialReasonCode = '')";

    private static readonly string ContradictoryApprovedRepairFilterPredicate =
        $"FROM c WHERE c.status = '{CosmosStatusLiteral(ClaimStatus.Approved)}' " +
        "AND IS_DEFINED(c.adjudicationResult) " +
        "AND c.adjudicationResult.payerPayment = 0 " +
        "AND ((IS_DEFINED(c.adjudicationResult.denialReasonCode) " +
        "AND NOT IS_NULL(c.adjudicationResult.denialReasonCode) " +
        "AND c.adjudicationResult.denialReasonCode != '') " +
        "OR (IS_DEFINED(c.adjudicationResult.denialReason) " +
        "AND NOT IS_NULL(c.adjudicationResult.denialReason) " +
        "AND c.adjudicationResult.denialReason != '') " +
        "OR (IS_DEFINED(c.adjudicationResult.adjustmentReasons) " +
        "AND ARRAY_LENGTH(c.adjudicationResult.adjustmentReasons) > 0))";

    /// <summary>Cosmos patch FilterPredicate for <see cref="IsFinalDisposition"/> — built from <see cref="FinalDispositions"/>; Pended is deliberately absent (re-pending is allowed).</summary>
    private static readonly string FinalDispositionFilterPredicate =
        BuildStatusNotInFilterPredicate(FinalDispositions);

    /// <summary>
    /// <paramref name="predicate"/> ("FROM c WHERE …"), additionally requiring
    /// the resolution lock <paramref name="lockToken"/> when one is given
    /// (an approval re-run's writes, PR #1278 follow-up 1).
    /// </summary>
    internal static string WithResolutionLockFence(string predicate, string? lockToken)
    {
        if (lockToken is null) return predicate;
        const string prefix = "FROM c WHERE ";
        var condition = predicate.StartsWith(prefix, StringComparison.Ordinal) ? predicate[prefix.Length..] : predicate;
        return $"{prefix}({condition}) AND c.resolutionLock.token = '{ResolutionLockTokenLiteral(lockToken)}'";
    }

    private static string BuildStatusNotInFilterPredicate(IReadOnlyList<ClaimStatus> blockedStatuses) =>
        $"FROM c WHERE NOT (c.status IN ({string.Join(",", blockedStatuses.Select(s => $"'{CosmosStatusLiteral(s)}'"))}))";

    private static string CosmosStatusLiteral(ClaimStatus status) => CosmosEnumLiteral(status);

    /// <summary>
    /// The spelling <c>CosmosSystemTextJsonSerializer</c> persists for an
    /// enum value: its options-level <c>JsonStringEnumConverter(CamelCase)</c>
    /// takes precedence over the PascalCase type-level converter on
    /// <see cref="ClaimVersionState"/> / <see cref="ClaimStatus"/>, so
    /// <c>Draft</c> is stored as <c>"draft"</c>. <c>value.ToString()</c> is
    /// therefore the wrong literal for a Cosmos query.
    /// </summary>
    internal static string CosmosEnumLiteral<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>
    /// Builds <c>{field} IN (@{prefix}0, …)</c> matching each value in both
    /// spellings (see <see cref="DuplicateExclusionLiterals"/>) and records
    /// the bound literals in <paramref name="parameters"/>.
    /// </summary>
    internal static string EnumInClause<TEnum>(
        string field, string paramPrefix, IDictionary<string, object> parameters, params TEnum[] values)
        where TEnum : struct, Enum
    {
        var literals = DuplicateExclusionLiterals(values.Select(v => v.ToString()));
        var names = new List<string>(literals.Count);
        for (var i = 0; i < literals.Count; i++)
        {
            var name = $"@{paramPrefix}{i}";
            parameters[name] = literals[i];
            names.Add(name);
        }
        return $"{field} IN ({string.Join(", ", names)})";
    }

    /// <summary>
    /// Inline-literal form of <see cref="EnumInClause{TEnum}"/> for
    /// expressions that can't take parameters (aggregate CASE arms). Enum
    /// names are identifiers, so inlining them is injection-safe.
    /// </summary>
    internal static string EnumInLiteral<TEnum>(string field, params TEnum[] values) where TEnum : struct, Enum =>
        $"{field} IN ({string.Join(", ", DuplicateExclusionLiterals(values.Select(v => v.ToString())).Select(l => $"'{l}'"))})";

    /// <summary>
    /// Both spellings of each enum name — the serializer's camelCase form
    /// (see <see cref="CosmosEnumLiteral{TEnum}"/>) and PascalCase — so
    /// filters match serializer-written rows and rows from older writers.
    /// </summary>
    internal static IReadOnlyList<string> DuplicateExclusionLiterals(IEnumerable<string> enumNames) =>
        enumNames
            .SelectMany(n => new[] { JsonNamingPolicy.CamelCase.ConvertName(n), n })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public async Task<AccumulatorTotalsResponse> GetAccumulatorTotalsAsync(
        string ownerId,
        string scope,
        string benefitPlanId,
        string planYear,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();

        // Standard calendar plan year — plan-year-aware plans should store explicit dates
        // but planYear == "2026" → Jan 1 2026 through Dec 31 2026 is the safe default.
        var yearStart = new DateTime(int.Parse(planYear), 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = new DateTime(int.Parse(planYear), 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // For Individual scope filter by memberId; for Family scope by subscriberId.
        var ownerFilter = scope == "Family"
            ? "c.subscriberId = @ownerId"
            : "c.memberId = @ownerId";

        // Filter on (a) the legacy ClaimStatus values that have always counted
        // toward accumulators AND (b) the new ClaimVersionState values that
        // map to the same operational notion. Either clause matching keeps a
        // row in the result set, so legacy unhydrated rows continue to count
        // and new versioned rows count once they reach Adjudicated/Paid.
        // Both enums are persisted camelCase by the Cosmos serializer, so each
        // clause binds the camelCase spelling (plus PascalCase for rows from
        // older writers); the former 'Approved' / "Adjudicated" literals
        // matched no serializer-written row.
        var enumParams = new Dictionary<string, object>();
        var statusClause = EnumInClause("c.status", "countedStatus", enumParams,
            ClaimStatus.Approved, ClaimStatus.PartiallyPaid, ClaimStatus.Paid);
        var versionStateClause = EnumInClause("c.versionState", "countedVersionState", enumParams,
            ClaimVersionState.Adjudicated, ClaimVersionState.Paid);
        // Superseded versions (Adjusted, or SupersededAt set) no longer count.
        var adjustedClause = EnumInClause("c.versionState", "supersededState", enumParams,
            ClaimVersionState.Adjusted);
        var queryText = $@"
            SELECT c.id,
                   c.adjudicationResult.deductibleAmount,
                   c.adjudicationResult.coinsuranceAmount,
                   c.adjudicationResult.copayAmount,
                   c.adjudicationResult.patientResponsibility,
                   (IS_NUMBER(c.adjudicationResult.oopAppliedAmount)
                        ? c.adjudicationResult.oopAppliedAmount
                        : c.adjudicationResult.patientResponsibility) AS oopAmount,
                   c.adjudicationResult.networkTier
            FROM c
            WHERE c.tenantId       = @tenantId
              AND {ownerFilter}
              AND c.benefitPlanId  = @benefitPlanId
              AND c.serviceDateFrom >= @yearStart
              AND c.serviceDateFrom <= @yearEnd
              AND (
                    {statusClause}
                    OR {versionStateClause}
                  )
              AND IS_DEFINED(c.adjudicationResult)
              AND (NOT IS_DEFINED(c.versionState) OR NOT ({adjustedClause}))
              AND (NOT IS_DEFINED(c.supersededAt) OR IS_NULL(c.supersededAt))";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId",      tenantId)
            .WithParameter("@ownerId",       ownerId)
            .WithParameter("@benefitPlanId", benefitPlanId)
            .WithParameter("@yearStart",     yearStart)
            .WithParameter("@yearEnd",       yearEnd);
        foreach (var (name, value) in enumParams) queryDef = queryDef.WithParameter(name, value);

        // Typed rows: the claims container's serializer is System.Text.Json,
        // under which a dynamic row is a JsonElement (member access on it
        // throws), so the projection binds to explicit property names.
        var iterator = _container.GetItemQueryIterator<AccumulatorTotalsQueryRow>(
            queryDef,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var rows = new List<AccumulatorTotalsRow>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var row in page)
            {
                // OOP-eligible amount when the engine recorded it (cost share
                // excluded from the OOP max doesn't count); legacy rows fall
                // back to full patient responsibility.
                rows.Add(new AccumulatorTotalsRow(
                    row.Id ?? string.Empty,
                    row.NetworkTier,
                    row.DeductibleAmount ?? 0m,
                    row.CoinsuranceAmount ?? 0m,
                    row.CopayAmount ?? 0m,
                    row.OopAmount ?? 0m));
            }
        }

        return AccumulatorTotalsRow.Build(rows, scope);
    }

    /// <summary>One row of the accumulator-totals projection.</summary>
    internal sealed class AccumulatorTotalsQueryRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [Newtonsoft.Json.JsonProperty("id")]
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("networkTier")]
        [Newtonsoft.Json.JsonProperty("networkTier")]
        public string? NetworkTier { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("deductibleAmount")]
        [Newtonsoft.Json.JsonProperty("deductibleAmount")]
        public decimal? DeductibleAmount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("coinsuranceAmount")]
        [Newtonsoft.Json.JsonProperty("coinsuranceAmount")]
        public decimal? CoinsuranceAmount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("copayAmount")]
        [Newtonsoft.Json.JsonProperty("copayAmount")]
        public decimal? CopayAmount { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("oopAmount")]
        [Newtonsoft.Json.JsonProperty("oopAmount")]
        public decimal? OopAmount { get; set; }
    }

    public async Task<bool> MarkSupersededProjectionAsync(
        string tenantId,
        string claimId,
        string supersessorVersionId,
        DateTime supersededAt,
        string? actorId,
        CancellationToken ct = default)
    {
        // Pre-read confirms the row exists and (defense in depth) belongs
        // to the supplied tenant. With the 5.1b /tenantId partition, a
        // cross-tenant claimId surfaces as Cosmos 404 (caught below); the
        // explicit TenantId equality check is intentionally retained
        // alongside the partition-key boundary for the same reasons
        // documented on GetByIdAsync.
        Claim? existing;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(claimId, new PartitionKey(tenantId), cancellationToken: ct);
            existing = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (existing == null || existing.TenantId != tenantId) return false;

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/supersededAt", supersededAt),
            PatchOperation.Set("/supersededByVersionId", supersessorVersionId),
            PatchOperation.Set("/versionState", ClaimVersionState.Adjusted),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
            PatchOperation.Set("/lastUpdatedBy", actorId),
        };

        try
        {
            await _container.PatchItemAsync<Claim>(
                claimId,
                new PartitionKey(tenantId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// A resolution lock token for a Cosmos FilterPredicate (which takes no
    /// parameters). Tokens are server-generated GUIDs; anything else is refused.
    /// </summary>
    internal static string ResolutionLockTokenLiteral(string token) =>
        token.Length is > 0 and <= 64 && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? token
            : throw new ArgumentException("Invalid resolution lock token.", nameof(token));

    public async Task<bool> HoldsResolutionLockAsync(
        string tenantId, string claimId, string token, DateTime now, CancellationToken ct = default)
    {
        try
        {
            var current = (await _container.ReadItemAsync<Claim>(claimId, new PartitionKey(tenantId), cancellationToken: ct)).Resource;
            return current.Status == ClaimStatus.Pended
                   && current.ResolutionLock is { } held
                   && string.Equals(held.Token, token, StringComparison.Ordinal)
                   && held.ExpiresAt > now;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> TryAcquireResolutionLockAsync(
        string tenantId, string claimId, string token, string? actorId, DateTime now, TimeSpan duration,
        CancellationToken ct = default)
    {
        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/resolutionLock", new ExaminerResolutionLock
            {
                Token = token, LockedBy = actorId, ExpiresAt = now + duration,
            }),
        };
        // ISO-8601 UTC strings compare in time order.
        var nowLiteral = now.ToUniversalTime().ToString("o");
        var options = new PatchItemRequestOptions
        {
            FilterPredicate =
                $"FROM c WHERE c.status = '{CosmosStatusLiteral(ClaimStatus.Pended)}' " +
                "AND (NOT IS_DEFINED(c.resolutionLock) OR IS_NULL(c.resolutionLock) " +
                $"OR c.resolutionLock.expiresAt < '{nowLiteral}')",
        };
        try
        {
            await _container.PatchItemAsync<Claim>(claimId, new PartitionKey(tenantId), ops, options, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Claim?> UpdateHoldingResolutionLockAsync(Claim claim, string lockToken, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;
        ItemResponse<Claim> current;
        try
        {
            current = await _container.ReadItemAsync<Claim>(claim.Id, new PartitionKey(tenantId), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (current.Resource.ResolutionLock?.Token != lockToken) return null;
        try
        {
            // The ETag makes the token check and the write one atomic step.
            var written = await _container.ReplaceItemAsync(claim, claim.Id, new PartitionKey(tenantId),
                new ItemRequestOptions { IfMatchEtag = current.ETag }, ct);
            return written.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task ReleaseResolutionLockAsync(string tenantId, string claimId, string token, CancellationToken ct = default)
    {
        var options = new PatchItemRequestOptions
        {
            FilterPredicate = $"FROM c WHERE IS_DEFINED(c.resolutionLock) AND c.resolutionLock.token = '{token.Replace("'", "")}'",
        };
        try
        {
            await _container.PatchItemAsync<Claim>(claimId, new PartitionKey(tenantId),
                [PatchOperation.Set<ExaminerResolutionLock?>("/resolutionLock", null)], options, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                             or System.Net.HttpStatusCode.NotFound)
        {
            // Not ours any more (or gone): nothing to release.
        }
    }

    public async Task<bool> MarkVoidedProjectionAsync(
        string tenantId,
        string claimId,
        DateTime voidedAt,
        string? actorId,
        CancellationToken ct = default)
    {
        Claim? existing;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(claimId, new PartitionKey(tenantId), cancellationToken: ct);
            existing = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (existing == null || existing.TenantId != tenantId) return false;

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ClaimStatus.Voided),
            PatchOperation.Set("/versionState", ClaimVersionState.Voided),
            PatchOperation.Set("/lastUpdatedDate", voidedAt),
            PatchOperation.Set("/lastUpdatedBy", actorId),
        };

        try
        {
            await _container.PatchItemAsync<Claim>(
                claimId,
                new PartitionKey(tenantId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Upper bound on rows <see cref="FindDuplicateCandidatesAsync"/> returns.
    /// A member's live claims overlapping one service period are normally a
    /// handful; the cap keeps a pathological member (or a bad date range)
    /// from turning one adjudication into an unbounded scan.
    /// </summary>
    public const int MaxDuplicateCandidates = 200;

    public async Task<IReadOnlyList<Claim>> FindDuplicateCandidatesAsync(
        string tenantId,
        string memberId,
        DateTime serviceDateFrom,
        DateTime serviceDateTo,
        string excludeClaimVersionId,
        CancellationToken ct = default)
    {
        // Single-partition (/tenantId) query: equality on memberId plus a
        // service-period overlap range — served by the container's default
        // range indexes. Undefined-vs-anything comparisons evaluate to
        // undefined in Cosmos SQL (≠ true), so each optional/legacy field is
        // guarded with NOT IS_DEFINED / null to keep legacy rows visible.
        //
        // Dead-row exclusion must match the persisted enum spelling, or dead
        // rows survive the filter and can fill the TOP cap ahead of a live
        // duplicate. The Cosmos serializer writes enums camelCase
        // (CosmosStatusLiteral); PascalCase is also excluded so rows written
        // by older serializers can never leak through.
        var deadStatuses = DuplicateExclusionLiterals(
            new[] { ClaimStatus.Denied, ClaimStatus.Voided }.Select(s => s.ToString()));
        var deadVersionStates = DuplicateExclusionLiterals(
            new[]
            {
                ClaimVersionState.Draft,
                ClaimVersionState.Denied,
                ClaimVersionState.Voided,
                ClaimVersionState.Adjusted,
            }.Select(s => s.ToString()));

        var statusParams = deadStatuses.Select((_, i) => $"@deadStatus{i}").ToList();
        var versionStateParams = deadVersionStates.Select((_, i) => $"@deadVersionState{i}").ToList();

        var query = new QueryDefinition($@"
            SELECT TOP {MaxDuplicateCandidates} *
            FROM c
            WHERE c.tenantId = @tenantId
              AND c.memberId = @memberId
              AND c.serviceDateFrom <= @serviceDateTo
              AND c.serviceDateTo >= @serviceDateFrom
              AND c.id != @excludeChain
              AND (NOT IS_DEFINED(c.claimVersionId) OR c.claimVersionId = null OR c.claimVersionId != @excludeChain)
              AND NOT (c.status IN ({string.Join(", ", statusParams)}))
              AND (NOT IS_DEFINED(c.versionState) OR c.versionState = null
                   OR NOT (c.versionState IN ({string.Join(", ", versionStateParams)})))
              AND (NOT IS_DEFINED(c.supersededAt) OR c.supersededAt = null)
              AND (NOT IS_DEFINED(c.claimFrequencyCode) OR c.claimFrequencyCode = null OR c.claimFrequencyCode != '8')")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId)
            .WithParameter("@serviceDateFrom", serviceDateFrom)
            .WithParameter("@serviceDateTo", serviceDateTo)
            .WithParameter("@excludeChain", excludeClaimVersionId);
        for (var i = 0; i < deadStatuses.Count; i++)
            query = query.WithParameter(statusParams[i], deadStatuses[i]);
        for (var i = 0; i < deadVersionStates.Count; i++)
            query = query.WithParameter(versionStateParams[i], deadVersionStates[i]);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var items = new List<Claim>();
        while (iterator.HasMoreResults && items.Count < MaxDuplicateCandidates)
        {
            var page = await iterator.ReadNextAsync(ct);
            items.AddRange(page);
        }

        return items.Take(MaxDuplicateCandidates).Select(Hydrate).ToList();
    }

    // internal (not private) so query-text contract tests can stub its iterator.
    internal sealed class HeadIdResult
    {
        public string Id { get; set; } = string.Empty;
        public ClaimStatus Status { get; set; }
    }
}
