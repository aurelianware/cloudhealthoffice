using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
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

    /// <summary>Member resolved by <see cref="IMemberResolver"/> at orchestrator entry.</summary>
    public ResolvedMember? ResolvedMember { get; set; }

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
}
