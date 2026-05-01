using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Persists the in-flight <see cref="ClaimAdjudicationContext.AdjudicationResult"/>
/// + <see cref="ClaimAdjudicationContext.LineAdjudicationResults"/> via
/// <see cref="IClaimRepository.UpdateAdjudicationProjectionAsync"/> — the
/// 5.1a projection-metadata bypass method.
///
/// <para>
/// First production consumer of that bypass; the 5th instance of the
/// projection-bypass pattern across the platform (Provider integrity,
/// Provider credentialing, Provider panel-gating, BP network tiers,
/// claims adjudication). Same justification: adjudication state is
/// operationally distinct from claim identity, and a routine
/// adjudication run must not produce a new claim version row — that
/// surface is reserved for adjustments (5.12) and reversals.
/// </para>
///
/// <para>
/// PersistenceStage runs even when an upstream stage short-circuited the
/// pipeline — capturing the rejection / denial reason on the version is
/// the entire point of the audit chain. The orchestrator enforces that
/// invariant by checking <see cref="IClaimAdjudicationStage.IsRequired"/>
/// after the short-circuit decision.
/// </para>
/// </summary>
public sealed class PersistenceStage : IClaimAdjudicationStage
{
    public const string StageName = "Persistence";

    private readonly IClaimRepository _repository;
    private readonly ILogger<PersistenceStage> _logger;

    public PersistenceStage(
        IClaimRepository repository,
        ILogger<PersistenceStage> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 999;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        try
        {
            var written = await _repository
                .UpdateAdjudicationProjectionAsync(
                    context.TenantId,
                    context.ClaimVersionId,
                    context.AdjudicationResult,
                    context.LineAdjudicationResults,
                    ct,
                    pendDetails: context.PendDetails)
                .ConfigureAwait(false);

            if (!written)
            {
                _logger.LogWarning(
                    "Adjudication projection bypass returned false for claim {ClaimVersionId}; " +
                    "head row not found or version no longer current",
                    SanitizeForLog(context.ClaimVersionId));
                return ClaimAdjudicationStageResult.Reject(
                    StageName,
                    "Adjudication projection write failed — head version not found.");
            }

            return ClaimAdjudicationStageResult.Pass(StageName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist adjudication projection for claim {ClaimVersionId}",
                SanitizeForLog(context.ClaimVersionId));
            // Bubble the exception so the orchestrator's exception handler
            // abandons the Service Bus message — Service Bus then
            // redelivers and the next attempt re-runs the pipeline. If
            // the failure is permanent the message lands in DLQ after
            // MaxDeliveryCount.
            throw;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
