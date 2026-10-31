using ClaimsService.Models;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// Runs CHO's adjudication pipeline on a claim without any side effect, so its
/// answer can be compared with the answer of the core that adjudicated the
/// claim (QNXT, Facets, ...). Implemented by <see cref="ClaimAdjudicationOrchestrator"/>
/// so a shadow run and a real run share one pipeline and cannot drift apart.
/// </summary>
public interface IShadowAdjudicationPipeline
{
    /// <summary>
    /// Adjudicates <paramref name="claim"/> through
    /// <see cref="ClaimAdjudicationOrchestrator.ShadowSafeStages"/> only. The
    /// caller passes a detached copy with the core's result removed; the
    /// returned context holds CHO's result in memory and nothing else changes.
    /// </summary>
    Task<ClaimAdjudicationContext> EvaluateShadowAsync(
        string tenantId, AdapterClaim claim, string? actorId, string? correlationId, CancellationToken ct);
}
