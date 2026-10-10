using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// Concrete adjudication orchestrator. Resolves the claim through the
/// <see cref="ClaimAdapterFactory"/> (canonical read surface — vendor
/// systems adjudicate the same way once their adapters ship), resolves
/// member + plan once via the cached resolvers, then iterates the
/// registered stages in <see cref="IClaimAdjudicationStage.Order"/> order.
/// </summary>
public sealed class ClaimAdjudicationOrchestrator : IClaimAdjudicationOrchestrator, IClaimApprovalReadjudicator
{
    private readonly ClaimAdapterFactory _adapterFactory;
    private readonly IBenefitPlanResolver _planResolver;
    private readonly IMemberResolver _memberResolver;
    private readonly ICoverageResolver _coverageResolver;
    private readonly IReadOnlyList<IClaimAdjudicationStage> _stages;
    private readonly IClaimVersionEventPublisher _eventPublisher;
    private readonly IMessageBus _messageBus;
    private readonly IAdjudicationTenantContext _tenantContext;
    private readonly IClaimAdjustmentService _adjustmentService;
    private readonly AdjudicationPipelineOptions _options;
    private readonly ILogger<ClaimAdjudicationOrchestrator> _logger;
    private readonly IAccumulatorOutboxProcessor? _outbox;

    public ClaimAdjudicationOrchestrator(
        ClaimAdapterFactory adapterFactory,
        IBenefitPlanResolver planResolver,
        IMemberResolver memberResolver,
        ICoverageResolver coverageResolver,
        IEnumerable<IClaimAdjudicationStage> stages,
        IClaimVersionEventPublisher eventPublisher,
        IMessageBus messageBus,
        IAdjudicationTenantContext tenantContext,
        IClaimAdjustmentService adjustmentService,
        IOptions<AdjudicationPipelineOptions> options,
        ILogger<ClaimAdjudicationOrchestrator> logger,
        IAccumulatorOutboxProcessor? outbox = null)
    {
        _outbox = outbox;
        _adapterFactory = adapterFactory;
        _planResolver = planResolver;
        _memberResolver = memberResolver;
        _coverageResolver = coverageResolver;
        _stages = stages.OrderBy(s => s.Order).ToList();
        _eventPublisher = eventPublisher;
        _messageBus = messageBus;
        _tenantContext = tenantContext;
        _adjustmentService = adjustmentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task AdjudicateAsync(
        ClaimVersionSubmittedMessage message,
        MessageContext messageContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrEmpty(message.TenantId))
            throw new ArgumentException("TenantId is required", nameof(message));
        if (string.IsNullOrEmpty(message.ClaimVersionId))
            throw new ArgumentException("ClaimVersionId is required", nameof(message));

        // Pin the tenant id onto the scope so the engine + resolver
        // HTTP shims (which run from this background subscription, with
        // no HttpContext) can still send X-Tenant-ID downstream.
        _tenantContext.TenantId = message.TenantId;

        _logger.LogInformation(
            "Adjudication starting for tenant {TenantId} claim {ClaimVersionId} (delivery {DeliveryCount})",
            SanitizeForLog(message.TenantId), SanitizeForLog(message.ClaimVersionId),
            messageContext.DeliveryCount);

        var adapter = await _adapterFactory.GetAdapterAsync(message.TenantId, ct).ConfigureAwait(false);
        var adapterResponse = await adapter.GetClaimAsync(
            new ClaimAdapterRequest
            {
                TenantId = message.TenantId,
                ClaimId = message.ClaimId,
                ClaimVersionId = message.ClaimVersionId,
            },
            ct).ConfigureAwait(false);

        var claim = adapterResponse.Claim;
        if (claim is null)
        {
            _logger.LogWarning(
                "Claim {ClaimVersionId} not found via adapter; completing message with no work",
                SanitizeForLog(message.ClaimVersionId));
            return;
        }

        // Idempotency: a version with a meaningful terminal adjudication
        // projection can be skipped on redelivery. Some submit paths hydrate
        // an empty zero-valued AdjudicationResult placeholder on Submitted
        // claims; that is not evidence the async pipeline already ran.
        if (HasMeaningfulAdjudicationProjection(claim))
        {
            _logger.LogInformation(
                "Claim {ClaimVersionId} already adjudicated; skipping pipeline run",
                SanitizeForLog(message.ClaimVersionId));
            return;
        }

        var context = await PrepareContextAsync(
            message.TenantId, message.ClaimVersionId, claim,
            message.ActorId, message.CorrelationId ?? messageContext.CorrelationId, ct).ConfigureAwait(false);

        await RunPipelineAsync(context, ct).ConfigureAwait(false);
        await DriveAccumulatorOutboxAsync(context).ConfigureAwait(false);
        await EmitAdjudicatedEventAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The claim passed and its commit was written with its Approved status
    /// (the accumulator outbox): commit it now. Best effort — a failure (or a
    /// crash before this) leaves the entry on the claim for the dispatcher;
    /// the commit is idempotent on its id. If the status write did not apply,
    /// the claim holds no entry and nothing is committed.
    /// </summary>
    private async Task DriveAccumulatorOutboxAsync(ClaimAdjudicationContext context)
    {
        if (_outbox is null || context.PendingAccumulatorCommit is not { } item) return;
        try
        {
            await _outbox.ProcessAsync(context.TenantId, context.Claim.Id, AccumulatorOutboxKind.Commit, item.Id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Accumulator commit for claim {ClaimId} not attempted now; the outbox dispatcher retries it",
                SanitizeForLog(context.Claim.Id));
        }
    }

    private async Task<ClaimAdjudicationContext> PrepareContextAsync(
        string tenantId, string claimVersionId, AdapterClaim claim,
        string? actorId, string? correlationId, CancellationToken ct)
    {
        var context = new ClaimAdjudicationContext
        {
            TenantId = tenantId,
            ClaimVersionId = claimVersionId,
            Claim = claim,
            ActorId = actorId,
            CorrelationId = correlationId,
        };

        // The X12 837 on-ramp (ClaimsV1Controller.ImportRaw837 ->
        // X12837ClaimMapper) deliberately leaves BenefitPlanId blank rather
        // than guessing — resolve it from the member's active coverage here,
        // before plan resolution below, so a correctly-enrolled member's
        // claim still reaches BenefitCalculationStage with a real plan
        // instead of rejecting on "missing BenefitPlanId". Claims that
        // already carry a BenefitPlanId (JSON /import, MCC) are untouched.
        if (string.IsNullOrWhiteSpace(claim.BenefitPlanId) && !string.IsNullOrWhiteSpace(claim.MemberId))
        {
            var resolvedPlanId = await _coverageResolver
                .ResolveBenefitPlanIdAsync(
                    tenantId, claim.MemberId, claim.ServiceDateFrom,
                    MapInsuranceLineCode(claim.ClaimType), ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(resolvedPlanId))
            {
                claim.BenefitPlanId = resolvedPlanId;
            }
        }

        if (!string.IsNullOrWhiteSpace(claim.BenefitPlanId))
        {
            context.ResolvedPlan = await _planResolver
                .GetPlanAsync(tenantId, claim.BenefitPlanId!, ct)
                .ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(claim.MemberId))
        {
            context.ResolvedMember = await _memberResolver
                .GetMemberAsync(tenantId, claim.MemberId, ct)
                .ConfigureAwait(false);
        }

        return context;
    }

    /// <summary>
    /// Stages whose Pend is a review an examiner resolves by approving the
    /// claim (possible duplicate, provider integrity, network, NCCI edits,
    /// scrubbing). On an approval re-run their Pend becomes Pass — but only
    /// when the examiner reviewed that pend (PR #1278 round 3, B1): its code
    /// and exact reason are in the claim's persisted pend
    /// (<see cref="ExaminerApproval.ReviewedPend"/>), and it is not a
    /// transient failure (<see cref="IsTransientFailure"/>, retried instead).
    /// Anything else — a pend the examiner never saw — keeps
    /// the claim pended and the approval is refused for re-review. Not in the
    /// set: pricing and benefit calculation (a pend there means the amounts
    /// could not be computed; benefit calculation applies the same reviewed
    /// rule itself to its retro-plan / subrogation / spend-down pends) and
    /// coordination of benefits (its pends need an examiner-confirmed payer
    /// order — see <see cref="ExaminerApproval.PayerSequence"/>). The AI
    /// examination stage is advisory and is skipped on an approval re-run.
    /// </summary>
    private static readonly HashSet<string> ExaminerOverridableStages = new(StringComparer.Ordinal)
    {
        Stages.DuplicateClaimStage.StageName,
        Stages.ProviderIntegrityStage.StageName,
        Stages.NetworkCredentialingStage.StageName,
        Stages.NcciEditsStage.StageName,
        Stages.ScrubbingStage.StageName,
    };

    // Every override needs the exact reason the examiner reviewed, not just
    // the code (round-3 verification, M4 — NETWORK, SCRUB, NCCI and MUE as
    // well as MEDREVIEW, DUPLICATE, RETROELIG, SUBRO, SPENDDOWN): a different
    // edit, credentialing finding or duplicate is a new finding.

    /// <summary>
    /// Pend reasons that mean a check could not run (a service was
    /// unreachable or threw), not a finding an examiner can rule on. They are
    /// never overridden by an approval: the re-run retries the check, and the
    /// approval passes only once it succeeds (round-3 verification, M4).
    /// </summary>
    private static readonly string[] TransientFailureMarkers =
    [
        Stages.ProviderIntegrityStage.UnreachableReason,
        Stages.DuplicateClaimStage.LookupFailedReason,
        Stages.NetworkCredentialingStage.MembershipUnavailableReason,
        Stages.NetworkCredentialingStage.CredentialingUnavailableReason,
        "NCCI engine threw",
        Stages.CoordinationOfBenefitsStage.CoverageServiceUnavailablePendReason,
        "Coverage-service unavailable",
        Stages.BenefitCalculationStage.DeferredCommitUnsupportedReason,
    ];

    /// <summary>See <see cref="TransientFailureMarkers"/>.</summary>
    internal static bool IsTransientFailure(string? reason) =>
        reason is not null
        && TransientFailureMarkers.Any(m => reason.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pend codes for stages that pend without recording one on
    /// <see cref="ClaimAdjudicationContext.PendDetails"/>, so every pend is
    /// persisted with a code the examiner sees and a later approval can name.
    /// </summary>
    private static string PendCodeForStage(string stageName) => stageName switch
    {
        Stages.NetworkCredentialingStage.StageName => "NETWORK",
        Stages.ScrubbingStage.StageName => "SCRUB",
        _ => stageName.ToUpperInvariant() is { Length: > 20 } n ? n[..20] : stageName.ToUpperInvariant(),
    };

    /// <summary>
    /// Examiner approval of a pended claim: re-runs the whole pipeline with
    /// the examiner's decision applied (review pends cleared, a COB pend
    /// resolved by the payer order the examiner confirmed), so the final
    /// payment and accumulators come from a fresh pricing — never from the
    /// pended one. Persistence writes the result (fenced on the resolution
    /// lock); nothing is written to the accumulators here: on
    /// <see cref="ClaimAdjudicationOutcome.Pass"/> the result carries the
    /// prepared commit, which the caller makes after its own fenced final
    /// write.
    /// </summary>
    public async Task<ApprovalReadjudicationResult> ReadjudicateForApprovalAsync(
        string tenantId, string claimId, ExaminerApproval approval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(approval);
        _tenantContext.TenantId = tenantId;

        var adapter = await _adapterFactory.GetAdapterAsync(tenantId, ct).ConfigureAwait(false);
        var claim = (await adapter.GetClaimAsync(
            new ClaimAdapterRequest { TenantId = tenantId, ClaimId = claimId },
            ct).ConfigureAwait(false)).Claim;
        if (claim is null)
            return new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Reject, $"Claim {claimId} not found.");

        // The pended projection is replaced by this run.
        claim.AdjudicationResult = null;
        var context = await PrepareContextAsync(
            tenantId, claim.ClaimVersionId is { Length: > 0 } v ? v : claim.Id, claim,
            approval.ExaminerId, approval.CorrelationId, ct).ConfigureAwait(false);
        context.ExaminerApproval = approval;

        await RunPipelineAsync(context, ct).ConfigureAwait(false);

        // PR #1279 B1: the fenced write was refused — another resolver holds
        // the lock and owns the outcome. Emitting this run's Reject (audit,
        // Service Bus, adjustment callback) would fail an adjustment the new
        // holder is about to finalize. Emit nothing.
        if (context.ResolutionLockLost)
        {
            _logger.LogWarning(
                "Approval re-run for claim {ClaimId} lost its resolution lock; nothing emitted",
                SanitizeForLog(claimId));
            return new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Reject,
                "The examiner resolution lock is no longer held.")
            {
                ResolutionLockLost = true,
            };
        }

        await EmitAdjudicatedEventAsync(context, ct).ConfigureAwait(false);

        var outcome = ResolveFinalOutcome(context);
        var reasons = context.StageResults
            .Where(r => r.Outcome != ClaimAdjudicationOutcome.Pass && !string.IsNullOrEmpty(r.Reason))
            .Select(r => $"{r.StageName}: {r.Reason}")
            .ToList();
        return new ApprovalReadjudicationResult(outcome, reasons.FirstOrDefault(), reasons)
        {
            OverriddenPends = context.ExaminerOverrides.ToList(),
            PreparedAccumulatorCommit = outcome == ClaimAdjudicationOutcome.Pass
                ? context.BenefitResolutionResult?.PreparedAccumulatorCommit
                : null,
        };
    }

    private static bool HasMeaningfulAdjudicationProjection(AdapterClaim claim)
    {
        var result = claim.AdjudicationResult;
        if (result is null)
        {
            return false;
        }

        if (claim.Status is ClaimStatus.Approved
            or ClaimStatus.Denied
            or ClaimStatus.Paid
            or ClaimStatus.PartiallyPaid
            or ClaimStatus.Pended
            or ClaimStatus.Voided)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(result.NetworkTier)
            || result.AllowedAmount != 0
            || result.DeductibleAmount != 0
            || result.CoinsuranceAmount != 0
            || result.CopayAmount != 0
            || result.PatientResponsibility != 0
            || result.PayerPayment != 0
            || !string.IsNullOrWhiteSpace(result.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(result.DenialReason)
            || result.AdjustmentReasons.Count > 0
            || result.RemarkCodes.Count > 0
            || !string.IsNullOrWhiteSpace(result.CheckNumber)
            || result.PaymentDate.HasValue;
    }

    private async Task RunPipelineAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        foreach (var stage in _stages)
        {
            var enabled = IsEnabled(stage);
            var shouldSkip = !enabled
                || (context.ShortCircuited && !stage.IsRequired)
                // The AI examination is advice for the examiner; on the
                // examiner's own approval re-run it has nothing to add (L8).
                || (context.ExaminerApproval is not null && stage.Name == Stages.AiExaminationStage.StageName);

            if (shouldSkip)
            {
                _logger.LogDebug(
                    "Skipping stage {Stage} for claim {ClaimVersionId} " +
                    "(enabled={Enabled}, shortCircuited={ShortCircuited}, required={Required})",
                    stage.Name, SanitizeForLog(context.ClaimVersionId),
                    enabled, context.ShortCircuited, stage.IsRequired);
                continue;
            }

            var pendBefore = context.PendDetails;
            var codeBefore = pendBefore?.PendCode;
            var reasonBefore = pendBefore?.PendReason;
            var additionalBefore = pendBefore?.AdditionalPendReasons.Count ?? 0;

            ClaimAdjudicationStageResult result;
            try
            {
                result = await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!stage.IsRequired)
            {
                _logger.LogError(ex,
                    "Stage {Stage} threw for claim {ClaimVersionId}; treating as Reject",
                    stage.Name, SanitizeForLog(context.ClaimVersionId));
                result = ClaimAdjudicationStageResult.Reject(
                    stage.Name, $"{stage.Name} threw: {ex.GetType().Name}");
            }

            if (result.Outcome == ClaimAdjudicationOutcome.Pend)
            {
                var (pendCode, pendReason) = IdentifyPend(context, stage.Name, result, pendBefore, codeBefore, reasonBefore, additionalBefore);

                // Examiner approval re-run: a pend the examiner reviewed
                // passes (recorded for the audit) so the claim prices in
                // Production and finalizes. A pend they did not review stays
                // a Pend and the approval is refused.
                if (context.ExaminerApproval is { } approval
                    && ExaminerOverridableStages.Contains(stage.Name)
                    && !IsTransientFailure(pendReason)
                    && approval.Reviewed(pendCode, pendReason, exactReason: true))
                {
                    context.ExaminerOverrides.Add($"{stage.Name}: {pendCode}: {pendReason}");
                    result = new ClaimAdjudicationStageResult
                    {
                        StageName = result.StageName,
                        Continue = true,
                        Outcome = ClaimAdjudicationOutcome.Pass,
                        Reason = result.Reason,
                        Notes = [.. result.Notes, $"Pend resolved by examiner approval: {pendCode}: {pendReason}"],
                    };
                }
            }

            context.StageResults.Add(result);

            if (!result.Continue)
            {
                _logger.LogInformation(
                    "Stage {Stage} short-circuited pipeline for claim {ClaimVersionId} " +
                    "(outcome={Outcome}, reason={Reason})",
                    stage.Name, SanitizeForLog(context.ClaimVersionId),
                    result.Outcome, SanitizeForLog(result.Reason));
                context.ShortCircuited = true;
            }
        }
    }

    /// <summary>
    /// The (code, reason) of the pend <paramref name="stageName"/> just
    /// returned: what it recorded on <see cref="ClaimAdjudicationContext.PendDetails"/>
    /// (a new pend, or an entry added to <see cref="PendDetails.AdditionalPendReasons"/>).
    /// A stage that recorded nothing gets its code (<see cref="PendCodeForStage"/>)
    /// recorded here, so the pend is persisted and the examiner sees it.
    /// The AI examination's pend is advice on an existing pend and records
    /// nothing.
    /// </summary>
    private static (string Code, string? Reason) IdentifyPend(
        ClaimAdjudicationContext context, string stageName, ClaimAdjudicationStageResult result,
        PendDetails? pendBefore, string? codeBefore, string? reasonBefore, int additionalBefore)
    {
        var pend = context.PendDetails;
        if (pend is not null && (!ReferenceEquals(pend, pendBefore) || !string.Equals(pend.PendCode, codeBefore, StringComparison.Ordinal)))
        {
            var identified = (pend.PendCode, pend.PendReason);
            if (pendBefore is not null && !string.IsNullOrWhiteSpace(codeBefore))
            {
                // The stage replaced an earlier pend (round-3 verification,
                // blocker 1): keep every pend. The earlier one stays the
                // claim's pend — it routes the work queue — and this one is
                // added to it, so the persisted pend (what the examiner
                // reviews and approves) is the full set, not only the last.
                if (ReferenceEquals(pend, pendBefore))
                {
                    // Same object, code overwritten in place: restore it.
                    pend = new PendDetails
                    {
                        PendCode = pend.PendCode,
                        PendReason = pend.PendReason,
                        PendedAt = pend.PendedAt,
                    };
                    pendBefore.PendCode = codeBefore!;
                    pendBefore.PendReason = reasonBefore;
                }
                MergeInto(pendBefore, pend);
                context.PendDetails = pendBefore;
            }
            return identified;
        }
        if (pend is not null && pend.AdditionalPendReasons.Count > additionalBefore)
        {
            var (code, reason) = ExaminerApproval.SplitEntry(pend.AdditionalPendReasons[^1]);
            if (code is not null) return (code, reason);
        }

        var stageCode = PendCodeForStage(stageName);
        var stageReason = result.Reason is { Length: > 500 } r ? r[..500] : result.Reason;
        if (stageName == Stages.AiExaminationStage.StageName)
            return (stageCode, stageReason);
        if (pend is null)
        {
            context.PendDetails = new PendDetails
            {
                PendCode = stageCode,
                PendReason = stageReason,
                PendedAt = DateTime.UtcNow,
            };
        }
        else
        {
            var entry = $"{stageCode}: {stageReason}";
            pend.AdditionalPendReasons.Add(entry.Length > 500 ? entry[..500] : entry);
        }
        return (stageCode, stageReason);
    }

    /// <summary>
    /// Adds <paramref name="later"/> (a pend a later stage recorded as a new
    /// <see cref="PendDetails"/>) to <paramref name="earlier"/>: its
    /// "{code}: {reason}" and its own additional reasons, and its NCCI /
    /// duplicate evidence. Nothing is added twice.
    /// </summary>
    internal static void MergeInto(PendDetails earlier, PendDetails later)
    {
        void Add(string entry)
        {
            if (entry.Length > 500) entry = entry[..500];
            var present = ExaminerApproval.ReviewedFrom(earlier)
                .Any(r => string.Equals($"{r.Code}: {r.Reason}", entry, StringComparison.Ordinal));
            if (!present) earlier.AdditionalPendReasons.Add(entry);
        }

        if (!string.IsNullOrWhiteSpace(later.PendCode))
            Add($"{later.PendCode}: {later.PendReason}");
        foreach (var entry in later.AdditionalPendReasons)
            Add(entry);
        foreach (var failure in later.EditFailures)
            earlier.EditFailures.Add(failure);
        foreach (var finding in later.DuplicateFindings)
            earlier.DuplicateFindings.Add(finding);
    }

    private async Task EmitAdjudicatedEventAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var finalOutcome = ResolveFinalOutcome(context);
        // Reason follows the same precedence rule as outcome
        // (Reject > Deny > Pend > Pass) so the emitted message's Outcome
        // and Reason agree on which stage drove the result.
        var finalReason = context.StageResults
            .Where(r => r.Outcome == finalOutcome && !string.IsNullOrEmpty(r.Reason))
            .Select(r => r.Reason!)
            .FirstOrDefault();

        // 1) Mongo append-only event — system-of-record audit chain. Same
        //    degraded-mode posture as the submission service: failure here
        //    leaves the claim adjudicated but with a gap in the version
        //    event chain (operators can backfill from logs).
        try
        {
            var domainClaim = context.Claim.ToClaim();
            domainClaim.AdjudicationResult = context.AdjudicationResult;
            // 5.7 — surface deterministic edit-failure pend reason on
            // the audit event when NCCI / MUE populated it. Mirrors the
            // PendDetails write through the projection bypass so
            // subscribers see the same shape as the head row.
            if (context.PendDetails is not null)
            {
                domainClaim.PendDetails = context.PendDetails;
            }
            await _eventPublisher
                .PublishVersionAdjudicatedAsync(domainClaim, context.ActorId, context.CorrelationId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionAdjudicated audit event emission failed for claim {ClaimVersionId}; " +
                "adjudication persisted, audit chain has a gap",
                SanitizeForLog(context.ClaimVersionId));
        }

        // 2) Service Bus topic emission — trigger transport for downstream
        //    capabilities (5.10 remittance, 5.12 adjustments). Failure does
        //    not unwind adjudication; same posture.
        var sbMessage = new ClaimVersionAdjudicatedMessage
        {
            TenantId = context.TenantId,
            ClaimId = context.Claim.Id,
            ClaimVersionId = context.ClaimVersionId,
            VersionNumber = context.Claim.VersionNumber,
            Outcome = finalOutcome.ToString(),
            Reason = finalReason,
            CorrelationId = context.CorrelationId,
        };

        // An approval re-run is a new adjudication of the same version: a
        // distinct MessageId, or Service Bus duplicate detection drops it
        // as a repeat of the original run's message (M6).
        var sendOptions = new SendOptions(
            MessageId: context.ExaminerApproval is { } approval
                ? $"adjudicated:{context.ClaimVersionId}:approval:{approval.ApprovalId}"
                : $"adjudicated:{context.ClaimVersionId}",
            CorrelationId: context.CorrelationId,
            Properties: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaimVersionEventTopics.MessageTypeProperty] = ClaimVersionMessageTypes.Adjudicated,
            });

        try
        {
            await _messageBus
                .SendAsync(ClaimVersionEventTopics.TopicName, sbMessage, sendOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionAdjudicated Service Bus emission failed for claim {ClaimVersionId}",
                SanitizeForLog(context.ClaimVersionId));
        }

        // 3) Adjustment lifecycle callback (5.12b Premise A — orchestrator
        //    -finalize callback). If the new version is the
        //    NewClaimId of an in-flight ClaimAdjustment, transition the
        //    adjustment from AwaitingReadjudication to PendingReversal
        //    (Pass/Deny) or Failed (Reject). No-op for fresh non-adjustment
        //    submissions. Failure is non-blocking: adjudication has already
        //    persisted; the lifecycle transition can be re-driven by a
        //    follow-up sweep (Phase 2) or operator intervention.
        try
        {
            await _adjustmentService
                .OnNewVersionFinalizedAsync(
                    context.TenantId,
                    context.Claim.Id,
                    finalOutcome,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Adjustment lifecycle callback failed for new version {ClaimId}; pipeline persisted, adjustment may stay in AwaitingReadjudication",
                SanitizeForLog(context.Claim.Id));
        }
    }

    private static ClaimAdjudicationOutcome ResolveFinalOutcome(ClaimAdjudicationContext context)
    {
        // Reject takes precedence over Deny over Pend. Pass only when no
        // non-pass result was recorded by any stage — including
        // PersistenceStage, whose Reject (e.g.
        // UpdateAdjudicationProjectionAsync returned false) MUST surface
        // on the emitted event so subscribers don't see a Pass for a
        // claim whose adjudication never persisted. Called here AFTER
        // every stage including Persistence has run (see
        // ClaimAdjudicationStageResult.ResolveOutcome, the shared
        // precedence rule; PersistenceStage itself calls it one stage
        // earlier to decide the ClaimStatus.Pended projection).
        return ClaimAdjudicationStageResult.ResolveOutcome(context.StageResults);
    }

    private bool IsEnabled(IClaimAdjudicationStage stage)
    {
        if (stage.IsRequired) return true;
        if (_options.EnabledStages is null || _options.EnabledStages.Count == 0) return true;
        return !_options.EnabledStages.TryGetValue(stage.Name, out var enabled) || enabled;
    }

    /// <summary>
    /// Maps a claim's type to coverage-service's InsuranceLineCode filter
    /// (HLT/DEN/VIS/LIF) so active-coverage resolution matches the right
    /// line when a member carries more than one. Professional and
    /// Institutional both mean medical (HLT) — the distinction is
    /// place-of-care, not benefit line.
    /// </summary>
    private static string? MapInsuranceLineCode(ClaimType claimType) => claimType switch
    {
        ClaimType.Professional or ClaimType.Institutional => "HLT",
        ClaimType.Dental => "DEN",
        _ => null,
    };

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
