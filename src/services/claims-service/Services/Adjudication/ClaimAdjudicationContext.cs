using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.PlanConfiguration;
using BenefitEngineModels = CloudHealthOffice.BenefitEngine.Models;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// Mutable context object that flows through the adjudication pipeline.
/// Stages append outcomes to <see cref="StageResults"/> and decorate the
/// in-flight <see cref="AdjudicationResult"/> / <see cref="LineAdjudicationResults"/>
/// that <see cref="Stages.PersistenceStage"/> writes back via the bypass
/// method <c>IClaimRepository.UpdateAdjudicationProjectionAsync</c>.
///
/// <para>
/// Lives only for the duration of one orchestrator invocation (one
/// <c>ClaimVersionSubmittedMessage</c>); not shared across messages or
/// across pods. Mutability is safe: stages run sequentially within a
/// single Service Bus message handler (Decision 5).
/// </para>
/// </summary>
public class ClaimAdjudicationContext
{
    public required string TenantId { get; init; }
    public required string ClaimVersionId { get; init; }
    public required AdapterClaim Claim { get; init; }
    public string? CorrelationId { get; init; }
    public string? ActorId { get; init; }

    /// <summary>Plan resolved by <see cref="IBenefitPlanResolver"/> at orchestrator entry.</summary>
    public ResolvedBenefitPlan? ResolvedPlan { get; set; }

    /// <summary>
    /// The claim names a benefit plan but benefit-plan-service gave no
    /// definitive answer for it (<see cref="BenefitPlanUnavailableException"/>;
    /// a definitive 404 is not flagged). The plan's prior-authorization
    /// flags (G-25) are then unknown: <see cref="Stages.BenefitCalculationStage"/>
    /// pends <c>PLANUNAVAIL</c> when they could put a line in scope.
    /// </summary>
    public bool PlanResolutionFailed { get; set; }

    /// <summary>
    /// The member's active coverage, resolved by <see cref="ICoverageResolver"/>
    /// at orchestrator entry: the benefit plan and the sponsor hierarchy the
    /// claim sits under (plan-configuration.md §12.16). What
    /// <see cref="SettingScopeForClaim"/> is built from.
    ///
    /// <para>
    /// Null when the claim already carried a <c>BenefitPlanId</c> — the JSON
    /// <c>/import</c> and MCC on-ramps do, and
    /// <c>Adjudicate_ClaimWithExistingBenefitPlanId_DoesNotCallCoverageResolver</c>
    /// holds that those must not even query coverage — as well as when the
    /// lookup found nothing. A stage therefore cannot read this as "the member
    /// has no sponsor", only as "this context does not know one", and a claim
    /// from those two on-ramps resolves settings at its benefit plan and line
    /// of business without the sponsor levels.
    /// </para>
    /// </summary>
    public CoverageResolution? ResolvedCoverage { get; set; }

    /// <summary>Member resolved by <see cref="IMemberResolver"/> at orchestrator entry.</summary>
    public ResolvedMember? ResolvedMember { get; set; }

    /// <summary>
    /// What the member lookup at orchestrator entry found. <see cref="MemberLookupOutcome.NotFound"/>
    /// denies (the patient is not the plan's insured); <see cref="MemberLookupOutcome.Unavailable"/>
    /// pends (an outage is never "eligible"). <see cref="MemberLookupOutcome.NotLookedUp"/> only
    /// for a context built without the orchestrator.
    /// </summary>
    public MemberLookupOutcome MemberLookup { get; set; } = MemberLookupOutcome.NotLookedUp;

    /// <summary>
    /// What <see cref="Stages.InpatientGroupingStage"/> (Order=240) decided
    /// about the stay's DRG, consumed by <see cref="Stages.PricingStage"/>.
    /// Null on a claim that stage did not run for.
    /// </summary>
    public Stages.InpatientGroupingOutcome? InpatientGrouping { get; set; }

    /// <summary>Populated by <see cref="Stages.BenefitCalculationStage"/>.</summary>
    public BenefitEngineModels.BenefitResolutionResult? BenefitResolutionResult { get; set; }

    /// <summary>
    /// Building <see cref="ClaimsService.Models.AdjudicationResult"/>; persisted
    /// by <see cref="Stages.PersistenceStage"/> via the bypass method.
    /// Initialised empty so stub stages can leave it untouched.
    /// </summary>
    public Models.AdjudicationResult AdjudicationResult { get; set; } = new();

    public List<Models.LineAdjudicationResult> LineAdjudicationResults { get; set; } = new();

    /// <summary>Append-only history of every stage's result for this run.</summary>
    public List<ClaimAdjudicationStageResult> StageResults { get; } = new();

    /// <summary>True once a non-persistence stage has short-circuited the run.</summary>
    public bool ShortCircuited { get; set; }

    /// <summary>
    /// Network-membership lookup for the billing provider, populated by
    /// <see cref="Stages.NetworkCredentialingStage"/> (capability 5.6) for
    /// the FIRST plan tier that matched. Null when no tier matched OR the
    /// upstream lookup degraded; consumed by
    /// <see cref="Stages.BenefitCalculationStage"/> via
    /// <see cref="MatchedNetworkTier"/> to drive cost-share tiering.
    /// </summary>
    public Resolution.NetworkMembership? BillingProviderNetworkMembership { get; set; }

    /// <summary>
    /// Network-membership lookup for the rendering provider when it differs
    /// from the billing provider. Populated by
    /// <see cref="Stages.NetworkCredentialingStage"/> so rendering-provider
    /// exclusions can deny without overwriting billing-provider tier context.
    /// </summary>
    public Resolution.NetworkMembership? RenderingProviderNetworkMembership { get; set; }

    /// <summary>
    /// Credentialing-status snapshot for the billing provider as of the
    /// claim's earliest service date. Populated by capability 5.6.
    /// </summary>
    public Resolution.CredentialingStatusSnapshot? BillingProviderCredentialingStatus { get; set; }

    /// <summary>
    /// Credentialing-status snapshot for the rendering provider when it differs
    /// from the billing provider. Populated by capability 5.6.
    /// </summary>
    public Resolution.CredentialingStatusSnapshot? RenderingProviderCredentialingStatus { get; set; }

    /// <summary>
    /// Plan tier the billing provider matched, or <c>null</c> when none
    /// matched (out-of-network). Set by capability 5.6 alongside
    /// <see cref="BillingProviderNetworkMembership"/>.
    /// </summary>
    public ResolvedNetworkTier? MatchedNetworkTier { get; set; }

    /// <summary>
    /// Per-line fee-schedule pricing populated by
    /// <see cref="Stages.PricingStage"/> (Order=250). Null until that stage
    /// runs (or when it is disabled). <see cref="Stages.BenefitCalculationStage"/>
    /// only calculates when this is present and
    /// <see cref="PricingOutcome.IsFullyPriced"/> — it never lets the engine
    /// fall back to allowed = billed.
    /// </summary>
    public PricingOutcome? PricingResult { get; set; }

    /// <summary>
    /// Per-check enforcement outcomes accumulated by
    /// <see cref="Stages.NetworkCredentialingStage"/>. Surfaced on the
    /// audit trail and consumed by remittance generation (capability 5.10)
    /// for adjustment-reason emission.
    /// </summary>
    public List<EnforcementOutcome> EnforcementOutcomes { get; } = new();

    /// <summary>
    /// Pre-adjudication scrubbing outcome populated by
    /// <see cref="Stages.ScrubbingStage"/> (capability 5.4). Null until
    /// the scrubbing stage runs; downstream stages may inspect for
    /// warning context but do not gate on it (Reject already short-circuits).
    /// </summary>
    public ScrubbingOutcome? ScrubbingResult { get; set; }

    /// <summary>
    /// Deterministic edit-failure pend reason populated by
    /// <see cref="Stages.NcciEditsStage"/> (capability 5.7). Null when
    /// NCCI / MUE produced no failures (or the stage was disabled).
    /// Forwarded to the projection-bypass write so the head row carries
    /// the snapshot for portal queries and downstream consumers (5.9 AI
    /// examiner, 5.10 remittance) — see
    /// <see cref="Repositories.IClaimRepository.UpdateAdjudicationProjectionAsync"/>.
    /// Distinct from <see cref="AdjudicationResult"/> by design (see
    /// <see cref="ClaimsService.Models.PendDetails"/>) so the
    /// deterministic reason cannot be silently overwritten by a
    /// downstream stage.
    /// </summary>
    public PendDetails? PendDetails { get; set; }

    private readonly HashSet<string> _unavailableCheckReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// The reasons recorded on this run for checks that could not run
    /// (<see cref="ClaimAdjudicationStageResult.AsCheckUnavailable"/>). These
    /// are the exact strings the stages wrote, so whether a pend is transient
    /// never depends on its wording.
    /// </summary>
    public IReadOnlyCollection<string> UnavailableCheckReasons => _unavailableCheckReasons;

    /// <summary>Records <paramref name="reason"/> as a check that could not run on this run.</summary>
    public void MarkCheckUnavailable(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _unavailableCheckReasons.Add(reason);
    }

    /// <summary>
    /// True when <paramref name="reason"/> (a pend reason, or a "CODE: reason"
    /// entry, possibly truncated) is one this run recorded as a check that
    /// could not run.
    /// </summary>
    public bool IsCheckUnavailable(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && _unavailableCheckReasons.Any(r => reason.Contains(r, StringComparison.Ordinal)
                                             || r.StartsWith(reason, StringComparison.Ordinal));

    /// <summary>
    /// Duplicate-claim findings recorded by
    /// <see cref="Stages.DuplicateClaimStage"/> when it pends. Held here as
    /// well as on <see cref="PendDetails"/> because later stages replace
    /// <see cref="PendDetails"/> wholesale; <see cref="Stages.PersistenceStage"/>
    /// re-attaches these before the projection write so the duplicate
    /// evidence is never lost.
    /// </summary>
    public List<DuplicateFindingSnapshot> DuplicateFindings { get; } = new();

    /// <summary>
    /// The plan's own edits <see cref="Stages.PlanEditsStage"/> pended the
    /// claim with (ADR 019). Held here as well as on <see cref="PendDetails"/>
    /// for the same reason as <see cref="DuplicateFindings"/>: a later stage
    /// may replace <see cref="PendDetails"/>, and PersistenceStage re-attaches these.
    /// </summary>
    public List<PlanEditFindingSnapshot> PlanEditFindings { get; } = new();

    /// <summary>
    /// Coordination of Benefits outcome populated by
    /// <see cref="Stages.CoordinationOfBenefitsStage"/> (capability 5.8).
    /// Null until the COB stage runs. Phase 1 (α posture, mirrors 5.4
    /// scrubbing): not persisted via PersistenceStage; lives on the
    /// context for downstream stages (5.9 AI examiner may consume) and
    /// for the <c>cho.claims.adjudication.cob.*</c> telemetry. Phase 2
    /// priorEob work extends <see cref="AdjudicationResult"/> with the
    /// CHO-secondary persistence fields.
    /// </summary>
    public CobOutcome? CobResult { get; set; }

    /// <summary>
    /// Set when this run re-adjudicates a pended claim an examiner approved
    /// (<see cref="IClaimApprovalReadjudicator"/>): review pends the examiner
    /// resolved pass, and the COB stage uses the payer order the examiner
    /// confirmed. Null on a normal run.
    /// </summary>
    public ExaminerApproval? ExaminerApproval { get; set; }

    /// <summary>
    /// Set by <see cref="Stages.PersistenceStage"/> when an approval re-run's
    /// fenced write was refused because the examiner resolution lock is no
    /// longer held (PR #1279 B1). Such a run must emit nothing — no audit
    /// event, no Service Bus message, no adjustment callback — because the
    /// resolver that took the lock over owns the claim's outcome.
    /// </summary>
    public bool ResolutionLockLost { get; set; }

    /// <summary>
    /// The accumulator outbox entry <see cref="Stages.PersistenceStage"/>
    /// asked to write with the claim's Approved status (null when none). The
    /// orchestrator drives it right after the run; if the status write did
    /// not apply, the claim holds no entry and nothing is committed.
    /// </summary>
    public AccumulatorOutboxItem? PendingAccumulatorCommit { get; set; }

    /// <summary>
    /// The pends an approval re-run overrode ("{stage}: {code}: {reason}"),
    /// for the persisted audit record. Empty outside an approval re-run.
    /// </summary>
    public List<string> ExaminerOverrides { get; } = new();

    /// <summary>
    /// AI-backed examination invocation outcome populated by
    /// <see cref="Stages.AiExaminationStage"/> (capability 5.9). Null
    /// until the stage runs. α posture (mirrors 5.4 scrubbing / 5.8 CoB):
    /// not persisted via PersistenceStage; lives on the context for the
    /// <c>cho.claims.adjudication.ai_examination.*</c> telemetry. The
    /// actual AI recommendation is written back to
    /// <see cref="ClaimsService.Models.Claim.AiExamination"/>
    /// asynchronously by claims-examiner-service via the existing
    /// <c>PUT /api/claims/{id}/ai-examination</c> endpoint — so this
    /// outcome captures only what the pipeline stage decided about
    /// invocation (filter pass/skip + Kafka emission), not the
    /// recommendation itself.
    /// </summary>
    public AiExaminationOutcome? AiExaminationResult { get; set; }

    /// <summary>
    /// The plan-configuration scope this claim resolves settings in
    /// (plan-configuration.md §12.16). Every stage reading a plan setting
    /// builds it here rather than assembling one of its own, so they cannot
    /// drift apart on which levels a claim is entitled to see.
    /// </summary>
    /// <remarks>
    /// Adjudication passed only the line of business, so a value set on a
    /// benefit plan, a sponsor or one of that sponsor's policies was invisible
    /// here however deliberately it had been entered.
    ///
    /// <para>
    /// The benefit plan comes from the claim rather than from
    /// <see cref="ResolvedCoverage"/>, because the orchestrator writes the
    /// resolved plan back onto the claim and the JSON <c>/import</c> and MCC
    /// on-ramps supply it directly — reading the coverage instead would leave
    /// those two on-ramps stuck at the line-of-business level.
    /// </para>
    ///
    /// <para>
    /// A policy is dropped when no sponsor came with it.
    /// <see cref="SettingScope.Incoherent"/> refuses such a scope — rightly,
    /// since answering it would skip the levels between the policy and the
    /// tenant — and tenant-service answers 422. Resolving at a shallower level
    /// is the right failure: a claim should not stop adjudicating because the
    /// hierarchy above it was only partly known.
    /// </para>
    /// </remarks>
    public SettingScope SettingScopeForClaim()
    {
        var sponsor = SponsorSettingNodes.ForSponsor(ResolvedCoverage?.GroupNumber);

        // A policy node is named {tenant}|{group}|{POLICY}, not by the bare
        // policy number -- see SponsorSettingNodes. ForPolicy answers null
        // without the group, which is also what keeps the scope coherent.
        var policy = sponsor is null
            ? null
            : SponsorSettingNodes.ForPolicy(TenantId, sponsor, ResolvedCoverage?.SponsorPolicyNumber);

        return new SettingScope
        {
            LineOfBusiness = Claim.LineOfBusiness.ToString(),
            BenefitPlan = Blank(Claim.BenefitPlanId) ? null : Claim.BenefitPlanId!.Trim(),
            Sponsor = sponsor,
            SponsorPolicy = policy,
        };
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>What the member lookup at the start of adjudication found.</summary>
public enum MemberLookupOutcome
{
    NotLookedUp,
    Found,

    /// <summary>member-service answered 404, or the claim names no member.</summary>
    NotFound,

    /// <summary>member-service gave no definitive answer.</summary>
    Unavailable,
}
