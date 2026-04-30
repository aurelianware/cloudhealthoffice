using ClaimsService.Models;
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
}
