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
public sealed class ClaimAdjudicationOrchestrator : IClaimAdjudicationOrchestrator
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
        ILogger<ClaimAdjudicationOrchestrator> logger)
    {
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

        var context = new ClaimAdjudicationContext
        {
            TenantId = message.TenantId,
            ClaimVersionId = message.ClaimVersionId,
            Claim = claim,
            ActorId = message.ActorId,
            CorrelationId = message.CorrelationId ?? messageContext.CorrelationId,
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
                    message.TenantId, claim.MemberId, claim.ServiceDateFrom,
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
                .GetPlanAsync(message.TenantId, claim.BenefitPlanId!, ct)
                .ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(claim.MemberId))
        {
            context.ResolvedMember = await _memberResolver
                .GetMemberAsync(message.TenantId, claim.MemberId, ct)
                .ConfigureAwait(false);
        }

        await RunPipelineAsync(context, ct).ConfigureAwait(false);
        await EmitAdjudicatedEventAsync(context, ct).ConfigureAwait(false);
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
                || (context.ShortCircuited && !stage.IsRequired);

            if (shouldSkip)
            {
                _logger.LogDebug(
                    "Skipping stage {Stage} for claim {ClaimVersionId} " +
                    "(enabled={Enabled}, shortCircuited={ShortCircuited}, required={Required})",
                    stage.Name, SanitizeForLog(context.ClaimVersionId),
                    enabled, context.ShortCircuited, stage.IsRequired);
                continue;
            }

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

        var sendOptions = new SendOptions(
            MessageId: $"adjudicated:{context.ClaimVersionId}",
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
