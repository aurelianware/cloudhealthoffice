using BenefitPlanService.Models;

namespace BenefitPlanService.Repositories;

/// <summary>
/// Storage seam for the benefit-plan version chain. Each row in the
/// underlying collection is one immutable version. Default reads (the
/// non-version-aware overloads kept for backward compatibility) resolve
/// to the latest <see cref="PlanVersionState.Published"/> version
/// effective today.
/// </summary>
public interface IBenefitPlanRepository
{
    Task<BenefitPlan?> GetByIdAsync(string id, string tenantId);

    /// <summary>
    /// Backward-compat: returns the latest <see cref="PlanVersionState.Published"/>
    /// version of <paramref name="planId"/> in effect right now. New code
    /// should call <see cref="GetLatestPublishedAsync"/> with an explicit
    /// <c>asOf</c> instead.
    /// </summary>
    Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId);

    Task<IEnumerable<BenefitPlan>> SearchAsync(string tenantId, string? lineOfBusiness, string? planType, string? metalLevel, int page, int pageSize);
    Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory);
    Task<BenefitPlan> CreateAsync(BenefitPlan plan);
    Task<BenefitPlan> UpdateAsync(BenefitPlan plan);
    Task DeleteAsync(string id, string tenantId);

    // ---- Version-chain operations ------------------------------------

    /// <summary>
    /// Latest <see cref="PlanVersionState.Published"/> version of
    /// <paramref name="planId"/> whose effective window contains
    /// <paramref name="asOf"/>. Returns null when no such version exists.
    /// </summary>
    Task<BenefitPlan?> GetLatestPublishedAsync(string planId, string tenantId, DateTime asOf);

    /// <summary>Look up a single version by <c>VersionId</c>.</summary>
    Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId);

    /// <summary>
    /// Newest-first list of every version for <paramref name="planId"/>,
    /// paginated with a continuation token.
    /// </summary>
    Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken);

    /// <summary>
    /// Persist a new draft. Caller is responsible for setting
    /// <c>VersionId</c>, <c>VersionNumber</c>, <c>VersionState=Draft</c>
    /// and (for amendments) <c>PredecessorVersionId</c>.
    /// </summary>
    Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft);

    /// <summary>Update a Draft. Throws <see cref="PlanVersionStateException"/> if the row is not Draft.</summary>
    Task<BenefitPlan> UpdateDraftAsync(BenefitPlan draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToPublish"/> from Draft
    /// to Published and (if not null) flip <paramref name="predecessor"/>
    /// from Published to Superseded with
    /// <c>SupersededByVersionId = draftToPublish.VersionId</c>. Implementations
    /// use a transactional batch (Cosmos) or session transaction (Mongo)
    /// when the backend supports it; otherwise they fall back to sequential
    /// writes and log a compensating-action warning.
    /// </summary>
    Task<BenefitPlan> PublishAndSupersedeAsync(BenefitPlan draftToPublish, BenefitPlan? predecessor);

    /// <summary>
    /// Projection-metadata bypass write: replaces the
    /// <see cref="BenefitPlan.NetworkTiers"/> collection on the head
    /// Published version of <paramref name="planId"/> without going
    /// through <see cref="UpdateAsync"/>. Used by the capability 5.5
    /// network-tier <c>NetworkId</c> backfill (and only by the
    /// backfill).
    ///
    /// <para>
    /// The Cosmos impl uses <c>PatchItemAsync</c> with a single
    /// field-scoped <c>Set</c> op; the Mongo impl uses
    /// <c>FindOneAndUpdateAsync</c> with a sort on
    /// <c>VersionNumber</c> and <c>$set</c> so the head row is
    /// resolved and patched in a single round-trip. No
    /// <c>PlanVersionEvent</c> is emitted — the operation is a
    /// projection-metadata refresh, not a chain transition. See
    /// <c>docs/architecture/plan-versioning.md</c> "Projection
    /// metadata — exempt from versioning".
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> when the head row was patched, <c>false</c>
    /// when no head Published row exists for the plan or the row was
    /// removed between lookup and patch (treated as a soft miss; the
    /// backfill records it under <c>not_found</c>).
    /// </para>
    /// </summary>
    Task<bool> UpdateNetworkTiersAsync(
        string tenantId,
        string planId,
        IReadOnlyList<NetworkTier> tiers,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a standalone termination: <paramref name="version"/> must
    /// already have <c>VersionState=Superseded</c>, <c>SupersededAt</c> set,
    /// <c>SupersededByVersionId=null</c> (no successor -- distinguishes a
    /// terminated plan from one replaced by an amendment), and
    /// <c>IsActive=false</c>, applied by the service layer. Mirrors
    /// <see cref="PublishAndSupersedeAsync"/>'s contract of taking a fully
    /// pre-mutated object and just persisting it. Returns <c>false</c> when
    /// the row was not found.
    /// </summary>
    Task<bool> TerminateVersionAsync(BenefitPlan version);
}

/// <summary>
/// Thrown when a write violates the version-state invariants — e.g. an
/// attempt to update a Published row, to publish a non-Draft row, or to
/// reach a version that doesn't exist. The controller boundary maps
/// <see cref="IsNotFound"/> to HTTP 404 and everything else to 409.
/// </summary>
public sealed class PlanVersionStateException : InvalidOperationException
{
    public string PlanId { get; }
    public string VersionId { get; }
    public PlanVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested plan/version
    /// does not exist", as opposed to a state-machine violation. Set on
    /// construction; controllers map this to HTTP 404 instead of 409.
    /// </summary>
    public bool IsNotFound { get; init; }

    public PlanVersionStateException(string planId, string versionId, PlanVersionState currentState, string message)
        : base(message)
    {
        PlanId = planId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}
