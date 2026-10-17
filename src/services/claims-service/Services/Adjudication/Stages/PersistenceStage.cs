using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

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
        // Defect A fix — the orchestrator's Pend decision (NCCI/MUE, COB) was
        // being computed correctly by upstream stages but never reached
        // ClaimStatus: this repository call previously had no way to know a
        // Pend had been decided. Resolve it here from every stage that ran
        // BEFORE Persistence (Order=999, always last) using the same
        // Reject>Deny>Pend>Pass precedence the orchestrator uses for the
        // emitted event's Outcome (ClaimAdjudicationStageResult.ResolveOutcome).
        // The repository uses a special one-way guard for Pend and the normal
        // guarded transition path for terminal Pass/Deny/Reject.
        var resolvedOutcome = ClaimAdjudicationStageResult.ResolveOutcome(context.StageResults);
        var isPend = resolvedOutcome == ClaimAdjudicationOutcome.Pend;
        var resolvedStatus = MapOutcomeToStatus(resolvedOutcome);

        EnsureDenialCode(context, resolvedOutcome);

        // Later stages (e.g. NCCI) replace PendDetails wholesale; re-attach
        // DuplicateClaimStage's findings so the duplicate evidence survives.
        if (context.DuplicateFindings.Count > 0
            && context.PendDetails is { DuplicateFindings.Count: 0 } pendDetails)
        {
            pendDetails.DuplicateFindings = context.DuplicateFindings.ToList();
        }

        // Accumulator outbox: a passing claim's prepared commit is written in
        // the same conditional status write that makes it Approved, then
        // driven by the outbox (immediately after this run, else by the
        // dispatcher). An examiner-approval re-run writes none here — the
        // resolver writes it with its lock-fenced final write.
        var pendingCommit = resolvedOutcome == ClaimAdjudicationOutcome.Pass
                            && context.ExaminerApproval is null
                            && context.BenefitResolutionResult?.PreparedAccumulatorCommit is { } prepared
            ? AccumulatorOutboxItem.ForCommit(prepared, DateTime.UtcNow)
            : null;
        context.PendingAccumulatorCommit = pendingCommit;

        // An examiner re-run of an adjudicated, not-yet-remitted claim (Pay /
        // Deny) that does not pass is refused by the resolver: the claim stays
        // exactly as it was (its adjudication result and status are not
        // overwritten by the refused attempt). A pended claim's re-run still
        // projects (it refreshes the pends the examiner sees).
        if (context.ExaminerApproval is not null
            && resolvedOutcome != ClaimAdjudicationOutcome.Pass
            && ClaimStatusSemantics.IsAdjudicatedNotFinalized(context.Claim.Status))
        {
            _logger.LogInformation(
                "Examiner re-run of {Status} claim {ClaimVersionId} resolved {Outcome}; not persisted (the resolver refuses it)",
                context.Claim.Status, LogSanitizer.Sanitize(context.ClaimVersionId), resolvedOutcome);
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        try
        {
            var written = await _repository
                .UpdateAdjudicationProjectionAsync(
                    context.TenantId,
                    context.ClaimVersionId,
                    context.AdjudicationResult,
                    context.LineAdjudicationResults,
                    ct,
                    pendDetails: context.PendDetails,
                    isPend: isPend,
                    // An examiner re-run never writes the status: the
                    // resolver's lock-fenced final write decides it (with the
                    // accumulator outbox entry that goes with it).
                    resolvedStatus: context.ExaminerApproval is null ? resolvedStatus : null,
                    resolvedBenefitPlanId: context.Claim.BenefitPlanId,
                    requiredResolutionLockToken: context.ExaminerApproval?.ResolutionLockToken,
                    pendingAccumulatorCommit: pendingCommit)
                .ConfigureAwait(false);

            if (!written && context.ExaminerApproval?.ResolutionLockToken is not null)
            {
                context.ResolutionLockLost = true;
                _logger.LogWarning(
                    "Approval re-run for claim {ClaimVersionId} did not persist: the examiner resolution lock " +
                    "is no longer held (another resolution took over)",
                    LogSanitizer.Sanitize(context.ClaimVersionId));
                return ClaimAdjudicationStageResult.Reject(
                    StageName,
                    "Adjudication projection write refused — the examiner resolution lock is no longer held.");
            }

            if (!written)
            {
                _logger.LogWarning(
                    "Adjudication projection bypass returned false for claim {ClaimVersionId}; " +
                    "head row not found or version no longer current",
                    LogSanitizer.Sanitize(context.ClaimVersionId));
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
                LogSanitizer.Sanitize(context.ClaimVersionId));
            // Bubble the exception so the orchestrator's exception handler
            // abandons the Service Bus message — Service Bus then
            // redelivers and the next attempt re-runs the pipeline. If
            // the failure is permanent the message lands in DLQ after
            // MaxDeliveryCount.
            throw;
        }
    }

    /// <summary>
    /// Safety net: a Deny/Reject persists as <see cref="ClaimStatus.Deny"/>
    /// (finalized to <see cref="ClaimStatus.Denied"/> by the payment run),
    /// and the 835 (payment-service <c>CarcRarcMappingService</c>) can only
    /// remit a denial that carries a CARC — a denied claim without one is
    /// excluded from the payment run. Every built-in denying stage sets its
    /// own code; if one reached persistence without (a new stage, or the
    /// orchestrator's catch-all Reject for a stage that threw) this assigns
    /// the documented default and logs a warning naming the stage so the gap
    /// is fixed at its source:
    /// <list type="bullet">
    ///   <item><description>Reject → CO-16 + MA130 (claim unprocessable;
    ///     correct and resubmit) — <see cref="AdjudicationDenialCodes.DefaultRejectCarc"/>.</description></item>
    ///   <item><description>Deny → CO-96 + N130 (non-covered; consult plan
    ///     benefits) — <see cref="AdjudicationDenialCodes.DefaultDenyCarc"/>.</description></item>
    /// </list>
    /// Only codes are assigned; the outcome / status is unchanged.
    /// </summary>
    internal void EnsureDenialCode(ClaimAdjudicationContext context, ClaimAdjudicationOutcome resolvedOutcome)
    {
        if (resolvedOutcome is not (ClaimAdjudicationOutcome.Deny or ClaimAdjudicationOutcome.Reject))
            return;
        if (!string.IsNullOrWhiteSpace(context.AdjudicationResult.DenialReasonCode))
            return;

        var deciding = context.StageResults.FirstOrDefault(r => r.Outcome == resolvedOutcome);
        var (carc, rarc) = resolvedOutcome == ClaimAdjudicationOutcome.Reject
            ? (AdjudicationDenialCodes.DefaultRejectCarc, AdjudicationDenialCodes.DefaultRejectRarc)
            : (AdjudicationDenialCodes.DefaultDenyCarc, AdjudicationDenialCodes.DefaultDenyRarc);

        _logger.LogWarning(
            "Stage {Stage} produced {Outcome} for claim {ClaimVersionId} without a denial reason code; " +
            "assigning default CARC {Carc} / RARC {Rarc}",
            deciding?.StageName ?? "unknown",
            resolvedOutcome,
            LogSanitizer.Sanitize(context.ClaimVersionId),
            carc,
            rarc);

        AdjudicationDenialCodes.Apply(
            context,
            carc,
            context.AdjudicationResult.DenialReason ?? deciding?.Reason,
            rarc);
    }

    private static ClaimStatus MapOutcomeToStatus(ClaimAdjudicationOutcome outcome) => outcome switch
    {
        ClaimAdjudicationOutcome.Pass => ClaimStatus.Approved,
        ClaimAdjudicationOutcome.Pend => ClaimStatus.Pended,
        // Deny, not Denied: the adjudicated denial is non-final (reprocess /
        // examiner resolve may still change it). The payment run's 835 and
        // its $0 remittance finalize it to Denied.
        ClaimAdjudicationOutcome.Deny => ClaimStatus.Deny,
        ClaimAdjudicationOutcome.Reject => ClaimStatus.Deny,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unexpected adjudication outcome."),
    };
}
