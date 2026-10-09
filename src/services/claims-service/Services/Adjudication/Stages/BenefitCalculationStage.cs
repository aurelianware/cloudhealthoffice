using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.CobEngine.Domain;
using CobInfo = CloudHealthOffice.BenefitEngine.Models.CobInfo;
using ClaimsAdj = ClaimsService.Models.AdjudicationResult;
using ClaimsLineAdj = ClaimsService.Models.LineAdjudicationResult;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 — the only "real" pipeline stage in this PR. Builds a
/// <see cref="BenefitResolutionRequest"/> from the in-flight
/// <see cref="ClaimAdjudicationContext"/>, calls
/// <see cref="IBenefitCalculationEngine.CalculateAsync"/> in Replace mode,
/// and writes the per-line + claim-level cost-share onto
/// <see cref="ClaimAdjudicationContext.AdjudicationResult"/> +
/// <see cref="ClaimAdjudicationContext.LineAdjudicationResults"/> for
/// <see cref="PersistenceStage"/> to persist.
///
/// <para>
/// Replace mode only in Phase 1 — Augment-mode comparison against legacy
/// adjudication ships in Phase 2 once there's a real legacy result to
/// compare against. The stage explicitly calls
/// <see cref="IBenefitCalculationEngine.CalculateAsync"/> rather than the
/// operating-mode-aware variant so the dependency surface stays minimal.
/// </para>
///
/// <para>
/// <b>Allowed amounts</b> come from <see cref="PricingStage"/> (Order 250)
/// via <see cref="ClaimAdjudicationContext.PricingResult"/>. When pricing
/// is missing or incomplete the stage pends without calling the engine —
/// an empty <c>AllowedAmounts</c> map would make the engine fall back to
/// allowed = billed (and write accumulators on that basis).
/// </para>
///
/// <para>
/// <b>Network tier</b> comes from <see cref="NetworkCredentialingStage"/>
/// (Order 200) — see <see cref="ResolveNetworkTier"/>.
/// </para>
/// </summary>
public sealed class BenefitCalculationStage : IClaimAdjudicationStage
{
    public const string StageName = "BenefitCalculation";
    public const string MemberNotEligibleCarc = "27";
    public const string PriorAuthorizationRequiredCode = "197";
    public const string PriorAuthorizationRequiredReason = "Prior authorization required but not provided";
    public const string PriorAuthorizationInvalidReason = "Prior authorization is not valid for this claim";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> value for claims pended because a
    /// retroactive benefit-plan/coverage change (X12 834 maintenance type
    /// code 001) was recorded with an effective date on or before the
    /// claim's own service date -- the plan in force on the service date
    /// can't be trusted without reconciliation.
    /// </summary>
    public const string RetroactivePlanChangePendCode = "RETROELIG";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> value for claims pended because the
    /// claim carries an X12 837 CLM11 related-causes code (auto accident,
    /// employment, or other accident) -- potential third-party liability
    /// requires subrogation investigation before the claim can pay.
    /// </summary>
    public const string SubrogationReviewPendCode = "SUBRO";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> value for claims pended because the
    /// member is enrolled under a Medicaid "medically needy" spend-down
    /// eligibility category and has not yet incurred enough medical expense
    /// in the current budget period to meet their spend-down liability --
    /// Medicaid coverage isn't confirmed active for this period yet.
    /// </summary>
    public const string MedicaidSpendDownPendCode = "SPENDDOWN";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> used when the claim reaches this
    /// stage without complete pricing and no earlier stage recorded a
    /// structured pend (e.g. the Pricing stage was disabled). Matches
    /// <see cref="PricingStage.PricingUnavailablePendCode"/>.
    /// </summary>
    public const string PricingRequiredPendCode = PricingStage.PricingUnavailablePendCode;

    private readonly IBenefitCalculationEngine _engine;
    private readonly IMemberResolver _memberResolver;
    private readonly IAuthorizationValidationClient _authorizationValidationClient;
    private readonly ILogger<BenefitCalculationStage> _logger;

    public BenefitCalculationStage(
        IBenefitCalculationEngine engine,
        IMemberResolver memberResolver,
        IAuthorizationValidationClient authorizationValidationClient,
        ILogger<BenefitCalculationStage> logger)
    {
        _engine = engine;
        _memberResolver = memberResolver;
        _authorizationValidationClient = authorizationValidationClient;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 300;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var claim = context.Claim;

        if (string.IsNullOrWhiteSpace(claim.BenefitPlanId))
        {
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                "Claim is missing BenefitPlanId; benefit calculation cannot run.");
        }

        var planGuid = ResolvePlanGuid(context.ResolvedPlan, claim.BenefitPlanId);
        if (planGuid is null)
        {
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                $"BenefitPlanId '{claim.BenefitPlanId}' is not a GUID and benefit-plan-service did not resolve a Guid id.");
        }

        if (!IsMemberEligibleForServiceDate(context.ResolvedMember, claim.ServiceDateFrom, out var eligibilityReason))
        {
            context.AdjudicationResult.DenialReasonCode = MemberNotEligibleCarc;
            context.AdjudicationResult.DenialReason = eligibilityReason;

            return ClaimAdjudicationStageResult.Deny(
                StageName,
                eligibilityReason);
        }

        // Review pends below are what an examiner resolves by approving the
        // claim (ExaminerApproval): the approval re-run skips them.
        var examinerApproved = context.ExaminerApproval is not null;

        if (!examinerApproved && HasUnreconciledRetroactivePlanChange(context.ResolvedMember, claim.ServiceDateFrom, out var pendReason))
        {
            context.PendDetails = new PendDetails
            {
                PendCode = RetroactivePlanChangePendCode,
                PendReason = pendReason,
                PendedAt = DateTime.UtcNow,
            };

            return ClaimAdjudicationStageResult.Pend(StageName, pendReason);
        }

        if (!examinerApproved && HasUnreviewedSubrogationIndicator(claim, out var subrogationReason))
        {
            context.PendDetails = new PendDetails
            {
                PendCode = SubrogationReviewPendCode,
                PendReason = subrogationReason,
                PendedAt = DateTime.UtcNow,
            };

            return ClaimAdjudicationStageResult.Pend(StageName, subrogationReason);
        }

        if (!examinerApproved && HasUnmetMedicaidSpendDown(context.ResolvedMember, out var spendDownReason))
        {
            context.PendDetails = new PendDetails
            {
                PendCode = MedicaidSpendDownPendCode,
                PendReason = spendDownReason,
                PendedAt = DateTime.UtcNow,
            };

            return ClaimAdjudicationStageResult.Pend(StageName, spendDownReason);
        }

        var priorAuthorizationDenialReason = await ResolvePriorAuthorizationDenialReasonAsync(
            context.TenantId,
            claim,
            ct).ConfigureAwait(false);

        if (priorAuthorizationDenialReason is not null)
        {
            context.AdjudicationResult.DenialReasonCode = PriorAuthorizationRequiredCode;
            context.AdjudicationResult.DenialReason = priorAuthorizationDenialReason;

            return ClaimAdjudicationStageResult.Deny(
                StageName,
                priorAuthorizationDenialReason);
        }

        // Fail closed on pricing: never let the engine fall back to
        // allowed = billed. PricingStage already pended with the specific
        // reason when it ran; this guard also covers the stage being
        // disabled via EnabledStages.
        if (context.PricingResult is not { IsFullyPriced: true })
        {
            var pricingReason = context.PricingResult is null
                ? "Claim lines were not priced (Pricing stage did not run); benefit calculation requires fee-schedule allowed amounts."
                : "Claim lines could not be fully priced; benefit calculation deferred until allowed amounts resolve.";
            context.PendDetails ??= new PendDetails
            {
                PendCode = PricingRequiredPendCode,
                PendReason = pricingReason,
                PendedAt = DateTime.UtcNow,
            };
            return ClaimAdjudicationStageResult.Pend(StageName, pricingReason);
        }

        // The 837 (SBR01) or coverage-service puts this plan after another
        // payer, but the COB stage did not clear COB for the claim (it pended
        // or denied, ran in a mode that passed without COB, or did not run).
        // Never price a later-payer claim as primary — unless an examiner
        // confirmed on approval that this plan is primary.
        if (IsLaterPayerWithoutCob(context))
        {
            const string cobReason =
                "This plan pays after another payer (837 SBR01 or coverage records), but coordination of " +
                "benefits was not cleared for the claim; benefit calculation deferred.";
            context.PendDetails ??= new PendDetails
            {
                PendCode = CoordinationOfBenefitsStage.CobPendCode,
                PendReason = cobReason,
                PendedAt = DateTime.UtcNow,
            };
            return ClaimAdjudicationStageResult.Pend(StageName, cobReason);
        }

        var subscriberId = await ResolveSubscriberIdAsync(context, ct).ConfigureAwait(false);
        var request = BuildRequest(context, planGuid.Value, subscriberId);

        BenefitResolutionResult result;
        try
        {
            result = await _engine.CalculateAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Benefit calculation engine threw for claim {ClaimVersionId}",
                SanitizeForLog(context.ClaimVersionId));
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                $"Benefit calculation engine threw: {ex.GetType().Name}");
        }

        context.BenefitResolutionResult = result;

        // The engine could not produce a balanced result (e.g. a per-stay
        // allocation allowing a line more than it billed). It wrote no
        // accumulators; pend for review instead of denying.
        if (result.RequiresReview)
        {
            var reviewReason = result.PendReason ?? "Benefit calculation requires manual review.";
            context.PendDetails ??= new PendDetails
            {
                PendCode = result.PendReasonCode ?? PricingRequiredPendCode,
                PendReason = reviewReason,
                PendedAt = DateTime.UtcNow,
            };
            return ClaimAdjudicationStageResult.Pend(StageName, reviewReason);
        }

        ApplyToContext(context, result);

        if (!result.Success)
        {
            return ClaimAdjudicationStageResult.Deny(
                StageName,
                result.DenialReasonDescription ?? result.DenialReasonCode ?? "Benefit denied");
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private static Guid? ResolvePlanGuid(ResolvedBenefitPlan? resolved, string benefitPlanId)
    {
        if (resolved?.PlanGuid is Guid resolvedGuid) return resolvedGuid;
        return Guid.TryParse(benefitPlanId, out var parsed) ? parsed : null;
    }

    private static bool IsMemberEligibleForServiceDate(
        ResolvedMember? member,
        DateTime serviceDate,
        out string reason)
    {
        var serviceDay = serviceDate.Date;

        if (member?.EffectiveDate is DateTime effectiveDate
            && serviceDay < effectiveDate.Date)
        {
            reason = "Service date before member coverage effective date";
            return false;
        }

        if (member?.TerminationDate is DateTime terminationDate
            && serviceDay > terminationDate.Date)
        {
            reason = "Service date after member coverage termination date";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(member?.EnrollmentStatus)
            && !member.EnrollmentStatus.Equals("Active", StringComparison.OrdinalIgnoreCase)
            && !member.EnrollmentStatus.Equals("Terminated", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Member status is {member.EnrollmentStatus}";
            return false;
        }

        if (member?.EnrollmentStatus?.Equals("Terminated", StringComparison.OrdinalIgnoreCase) is true
            && member.TerminationDate is null)
        {
            reason = "Member coverage terminated";
            return false;
        }

        reason = "Active coverage";
        return true;
    }

    private static bool HasUnreconciledRetroactivePlanChange(
        ResolvedMember? member,
        DateTime serviceDate,
        out string reason)
    {
        if (member?.PlanChangeEffectiveDate is DateTime planChangeEffectiveDate
            && serviceDate.Date >= planChangeEffectiveDate.Date)
        {
            reason =
                $"Member has a retroactive benefit-plan change effective {planChangeEffectiveDate.Date:yyyy-MM-dd}; " +
                "the plan in force on the service date requires reconciliation before this claim can adjudicate.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static readonly HashSet<string> RecognizedRelatedCausesCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AA", // Auto Accident
        "EM", // Employment
        "OA", // Other Accident
    };

    private static bool HasUnreviewedSubrogationIndicator(AdapterClaim claim, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(claim.RelatedCausesCode)
            && RecognizedRelatedCausesCodes.Contains(claim.RelatedCausesCode))
        {
            reason =
                $"Claim carries related-causes code '{claim.RelatedCausesCode}'; potential third-party " +
                "liability requires subrogation investigation before this claim can adjudicate.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool HasUnmetMedicaidSpendDown(ResolvedMember? member, out string reason)
    {
        if (member?.MedicaidSpendDownLiabilityAmount is decimal liability
            && member.MedicaidSpendDownAmountMet < liability)
        {
            reason =
                $"Member has a Medicaid spend-down liability of {liability:C} for the current budget " +
                $"period and has incurred {member.MedicaidSpendDownAmountMet:C} toward it; coverage is " +
                "not yet confirmed active for this period.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    internal static bool RequiresPriorAuthorizationDenial(AdapterClaim claim)
    {
        return RequiresPriorAuthorizationValidation(claim)
            && string.IsNullOrWhiteSpace(claim.PriorAuthorizationNumber);
    }

    private async Task<string?> ResolvePriorAuthorizationDenialReasonAsync(
        string tenantId,
        AdapterClaim claim,
        CancellationToken ct)
    {
        if (!RequiresPriorAuthorizationValidation(claim))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(claim.PriorAuthorizationNumber))
        {
            return PriorAuthorizationRequiredReason;
        }

        var procedureCode = claim.ClaimLines
            .OrderBy(line => line.LineNumber)
            .Select(line => line.ProcedureCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        var providerNpi = string.IsNullOrWhiteSpace(claim.RenderingProviderNPI)
            ? claim.BillingProviderNPI
            : claim.RenderingProviderNPI;

        var validation = await _authorizationValidationClient.ValidateAsync(
                tenantId,
                claim.PriorAuthorizationNumber,
                procedureCode,
                claim.ServiceDateFrom,
                providerNpi,
                ct)
            .ConfigureAwait(false);

        if (validation is null || validation.IsValid)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(validation.ValidationMessage)
            ? PriorAuthorizationInvalidReason
            : validation.ValidationMessage;
    }

    private static bool RequiresPriorAuthorizationValidation(AdapterClaim claim)
    {
        return claim.ClaimType is ClaimsService.Models.ClaimType.Institutional
            && claim.LineOfBusiness is LineOfBusiness.Medicaid
            && string.Equals(claim.PlaceOfServiceCode, "21", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolveSubscriberIdAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var direct = context.Claim.SubscriberId;
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        if (context.ResolvedMember is { } already)
        {
            return already.SubscriberMemberId
                ?? (already.IsSubscriber ? already.MemberId : context.Claim.MemberId);
        }

        var resolved = await _memberResolver
            .GetMemberAsync(context.TenantId, context.Claim.MemberId, ct)
            .ConfigureAwait(false);
        if (resolved is not null)
        {
            context.ResolvedMember = resolved;
            return resolved.SubscriberMemberId
                ?? (resolved.IsSubscriber ? resolved.MemberId : context.Claim.MemberId);
        }

        return context.Claim.MemberId;
    }

    internal static BenefitResolutionRequest BuildRequest(
        ClaimAdjudicationContext context,
        Guid planGuid,
        string subscriberId)
    {
        var claim = context.Claim;
        var serviceDate = DateOnly.FromDateTime(claim.ServiceDateFrom);

        var pointerToCode = claim.DiagnosisCodes
            .Where(d => !string.IsNullOrWhiteSpace(d.Code))
            .ToDictionary(d => d.PointerNumber, d => d.Code);

        var perStay = ResolvePerStayPricing(context);

        return new BenefitResolutionRequest
        {
            ClaimId = claim.Id,
            MemberId = claim.MemberId,
            SubscriberId = subscriberId,
            BenefitPlanId = planGuid,
            ServiceDate = serviceDate,
            NetworkTier = ResolveNetworkTier(context),
            Lines = claim.ClaimLines.Select(l => BuildLine(l, claim, pointerToCode)).ToList(),
            AllowedAmounts = context.PricingResult is { } pricing
                ? new Dictionary<int, decimal>(pricing.AllowedAmounts)
                : new Dictionary<int, decimal>(),
            ClaimType = MapClaimType(claim.ClaimType),
            TypeOfBill = claim.TypeOfBill,
            // Claims-service stores CLM05-1 (facility type) as the claim's
            // place of service on an institutional claim.
            PlaceOfServiceIsFacilityType = claim.ClaimType == ClaimsService.Models.ClaimType.Institutional,
            LineOfBusiness = (int)claim.LineOfBusiness,
            Member = BuildMemberContext(context.ResolvedMember, claim, serviceDate),
            // DRG / all-inclusive per-diem stays: cost share once per stay.
            DrgCode = perStay?.DrgCode,
            DrgAllowedAmount = perStay?.ClaimAllowed,
            LengthOfStay = perStay?.LengthOfStay,
            InpatientPricingMethod = perStay?.Method,
            // COB only when the COB stage cleared it: coverage-service and the
            // 837 agree on the payer order and the 837 carries complete
            // prior-payer data.
            Cob = context.CobResult is { ApplyCob: true } cleared ? BuildCob(claim, cleared.PayerSequence) : null,
            // A corrected version is priced without the accumulators of the
            // version it replaces (claims-service adjustment workflow).
            ReplacesClaimId = string.IsNullOrWhiteSpace(claim.PredecessorVersionId) ? null : claim.PredecessorVersionId,
            // A claim an earlier stage already pended (COB, duplicate, …) is
            // priced read-only: no accumulator is written for a claim that
            // will not finalize now. It is priced again when it is released.
            ExecutionMode = context.StageResults.Any(r => r.Outcome == ClaimAdjudicationOutcome.Pend)
                ? AdjudicationExecutionMode.Prospective
                : AdjudicationExecutionMode.Production,
        };
    }

    /// <summary>
    /// The engine's coordination-of-benefits input, from the claim's 837
    /// payer data: this plan's sequence from 2000B SBR01, and every other
    /// payer sequenced before it (2320 SBR01) with its 2320 AMT*D / CAS and
    /// 2430 SVD / CAS. Null — no COB — when this plan is the first payer, its
    /// sequence is unknown (SBR01 U or absent), or no earlier payer is on
    /// the claim. Standard (complementary) COB is the model: the claim does
    /// not carry the plan's COB method.
    /// </summary>
    /// <summary>
    /// True when the 837 (2000B SBR01) or coverage-service puts this plan
    /// after another payer and COB was not cleared (<see cref="CobOutcome.ApplyCob"/>)
    /// — except when an examiner confirmed on approval that it is primary.
    /// </summary>
    internal static bool IsLaterPayerWithoutCob(ClaimAdjudicationContext context)
    {
        var cob = context.CobResult;
        if (cob is { ApplyCob: true }) return false;
        if (cob is { ConfirmedByExaminer: true, PayerSequence: 1 }) return false;
        var claimSaysLater = PayerResponsibility.ToSequence(context.Claim.PayerResponsibilityCode) >= 2;
        var coverageSaysLater = cob?.Scenario is CobScenario.ChoSecondaryDetected or CobScenario.ChoTertiaryDetected;
        return claimSaysLater || coverageSaysLater;
    }

    internal static CobInfo? BuildCob(AdapterClaim claim, int? sequence = null)
    {
        var ourSequence = sequence ?? PayerResponsibility.ToSequence(claim.PayerResponsibilityCode);
        if (ourSequence is not >= 2)
            return null;

        var priorPayers = (claim.OtherPayers ?? [])
            .Select(p => (Payer: p, Sequence: PayerResponsibility.ToSequence(p.PayerResponsibilityCode)))
            .Where(p => p.Sequence is { } s && s < ourSequence)
            .OrderBy(p => p.Sequence)
            .Select(p => new PriorPayerAdjudication
            {
                Sequence = p.Sequence!.Value,
                PayerId = p.Payer.PayerId,
                PayerName = p.Payer.PayerName,
                ClaimPaidAmount = p.Payer.PaidAmount,
                ClaimAdjustments = p.Payer.ClaimAdjustments.Select(ToPriorPayerAdjustment).ToList(),
                Lines = p.Payer.LineAdjudications
                    .Select(l => new PriorPayerLineAdjudication
                    {
                        LineNumber = l.LineNumber,
                        PaidAmount = l.PaidAmount,
                        Adjustments = l.Adjustments.Select(ToPriorPayerAdjustment).ToList(),
                    })
                    .ToList(),
            })
            .ToList();
        if (priorPayers.Count == 0)
            return null;

        var primary = priorPayers[0];
        return new CobInfo
        {
            PayerSequence = ourSequence.Value,
            UseComplementaryModel = true,
            PrimaryPayerId = primary.PayerId,
            PrimaryPayerName = primary.PayerName,
            PriorPayers = priorPayers,
        };
    }

    private static PriorPayerAdjustment ToPriorPayerAdjustment(ClaimAdjustmentReason a) => new()
    {
        GroupCode = a.GroupCode,
        ReasonCode = a.ReasonCode,
        Amount = a.Amount,
    };

    private sealed record PerStayPricing(
        InpatientPricingMethod Method, decimal ClaimAllowed, string? DrgCode, int? LengthOfStay);

    /// <summary>
    /// When <see cref="PricingStage"/> priced an institutional claim as one
    /// claim-level amount (a DRG case rate or an all-inclusive per diem,
    /// allocated across the lines by billed charges — see
    /// <c>PricingResult.IsPerStayRate</c>), routes the claim through the
    /// benefit engine's claim-level inpatient path: cost sharing (one
    /// inpatient copay, deductible and coinsurance once) is computed on the
    /// claim's total allowed — every priced line, including any carve-out
    /// line priced off another schedule — under the stay's inpatient
    /// benefit, accumulators are written once, and the result is allocated
    /// back to the lines. Per-line pricing returns null (per-line path, as
    /// before).
    /// </summary>
    private static PerStayPricing? ResolvePerStayPricing(ClaimAdjudicationContext context)
    {
        var claim = context.Claim;
        if (claim.ClaimType != ClaimsService.Models.ClaimType.Institutional
            || context.PricingResult is not { IsFullyPriced: true } pricing
            || pricing.RawResult is null)
        {
            return null;
        }

        var perStayLines = pricing.RawResult.LineResults.Where(r => r.IsPerStayRate).ToList();
        if (perStayLines.Count == 0)
        {
            return null;
        }

        var method = perStayLines.Any(r => r.FeeScheduleType == CloudHealthOffice.FeeScheduleEngine.Domain.FeeScheduleType.Drg)
            ? InpatientPricingMethod.DrgCaseRate
            : InpatientPricingMethod.PerDiem;
        var drgCode = string.IsNullOrWhiteSpace(claim.Institutional?.DrgCode)
            ? null
            : claim.Institutional!.DrgCode!.Trim();

        return new PerStayPricing(
            method,
            pricing.AllowedAmounts.Values.Sum(),
            drgCode,
            claim.Institutional?.CalculateLengthOfStay());
    }

    /// <summary>
    /// Maps the claims-service <c>ClaimType</c> enum into the EDI-style
    /// codes the benefit engine expects (<c>"837P"</c> / <c>"837I"</c> /
    /// <c>"837D"</c>). The engine's DRG case-rate path checks
    /// <c>ClaimType is not "837I"</c>; mapping is therefore correctness-
    /// critical for institutional adjudication.
    /// </summary>
    internal static string MapClaimType(ClaimsService.Models.ClaimType type) => type switch
    {
        ClaimsService.Models.ClaimType.Professional => "837P",
        ClaimsService.Models.ClaimType.Institutional => "837I",
        ClaimsService.Models.ClaimType.Dental => "837D",
        _ => "837P",
    };

    private static ClaimLineInput BuildLine(
        AdapterClaimLine line,
        AdapterClaim claim,
        IReadOnlyDictionary<int, string> pointerToCode)
    {
        // POS falls back to the claim-level value when the line override
        // is missing — ServiceCategoryResolver uses POS for rule matching
        // and for system-level fallback inference, so dropping it would
        // shift category resolution. On an 837I the claim-level value is
        // CLM05-1 (facility type); the resolver knows that from the
        // request's ClaimType / TypeOfBill and does not read it as POS.
        var pos = !string.IsNullOrEmpty(line.PlaceOfServiceCode)
            ? line.PlaceOfServiceCode
            : claim.PlaceOfServiceCode;

        // Map the line's DiagnosisPointers (e.g. [1, 3]) to the
        // corresponding ICD codes from the claim-level diagnosis list.
        // Unknown pointers (no diagnosis at that position) drop silently
        // — the engine treats missing diagnoses as "no opinion" rather
        // than an error.
        var diagnosesForLine = line.DiagnosisPointers
            .Where(pointerToCode.ContainsKey)
            .Select(p => pointerToCode[p])
            .ToList();

        return new ClaimLineInput
        {
            LineNumber = line.LineNumber,
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers.ToList(),
            RevenueCode = line.RevenueCode,
            PlaceOfService = pos ?? string.Empty,
            // ChargeAmount is the LINE TOTAL (X12 837 SV102 / SV203, mapped
            // verbatim by X12837ClaimMapper; scrub rule AL002 sums it
            // without units against CLM02). Multiplying by units overstated
            // billed for any multi-unit line.
            BilledAmount = line.ChargeAmount,
            Units = line.Units,
            DiagnosisCodes = diagnosesForLine,
        };
    }

    private static MemberContext? BuildMemberContext(
        ResolvedMember? member,
        AdapterClaim claim,
        DateOnly serviceDate)
    {
        if (member is null && claim.DiagnosisCodes.Count == 0) return null;

        int? age = null;
        if (member?.DateOfBirth is DateTime dob)
        {
            // Age at the encounter, not at adjudication time. For
            // retrospective claims this can shift the age band by years
            // and change which benefit rules apply (pediatric / adult /
            // senior / Medicare-eligible).
            var encounter = serviceDate.ToDateTime(TimeOnly.MinValue);
            age = encounter.Year - dob.Year;
            if (dob.Date > encounter.AddYears(-age.Value)) age--;
        }

        BenefitMemberGender? gender = member?.Gender switch
        {
            "Female" or "F" => BenefitMemberGender.Female,
            "Male" or "M" => BenefitMemberGender.Male,
            "NonBinary" or "Other" => BenefitMemberGender.NonBinary,
            _ => null
        };

        var diagnoses = claim.DiagnosisCodes
            .Select(d => d.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        return new MemberContext
        {
            AgeYears = age,
            Gender = gender,
            DiagnosisCodes = diagnoses.Count > 0 ? diagnoses : null,
        };
    }

    internal static void ApplyToContext(
        ClaimAdjudicationContext context,
        BenefitResolutionResult result)
    {
        var totals = result.Totals;
        var existing = context.AdjudicationResult;

        context.AdjudicationResult = new ClaimsAdj
        {
            NetworkTier = NormalizeTier(ResolveNetworkTier(context)),
            AllowedAmount = totals.TotalAllowed,
            DeductibleAmount = totals.TotalDeductible,
            CoinsuranceAmount = totals.TotalCoinsurance,
            CopayAmount = totals.TotalCopay,
            PatientResponsibility = totals.TotalMemberResponsibility,
            OopAppliedAmount = totals.TotalOopApplied,
            // Set only when COB was applied: a primary claim leaves it null so
            // the finalized event carries no DeductibleCredited, and an older
            // benefit-plan-service (no such field → 0) can never zero out a
            // primary claim's deductible credit.
            DeductibleCreditedAmount = result.CobPayerSequence is null ? null : totals.TotalDeductibleCredited,
            CobPayerSequence = result.CobPayerSequence,
            PayerPayment = totals.TotalPlanPaid,
            DenialReasonCode = result.Success ? null : result.DenialReasonCode,
            DenialReason = result.Success ? null : result.DenialReasonDescription,
            AdjustmentReasons = existing.AdjustmentReasons,
            RemarkCodes = existing.RemarkCodes,
            CheckNumber = existing.CheckNumber,
            PaymentDate = existing.PaymentDate,
        };

        // Line CAS is load-bearing downstream: ClaimEventPublisher derives
        // each finalized line's deductible / coinsurance / copay from PR-1 /
        // PR-2 / PR-3 here, and accumulator-service prefers those line
        // amounts over the claim-level totals. Leaving the list empty
        // published a deductible delta of 0 for every engine-adjudicated
        // claim.
        var priorLines = context.LineAdjudicationResults;
        context.LineAdjudicationResults = OrderByClaimLines(context.Claim, result.Lines)
            .Select((l, i) => new ClaimsLineAdj
            {
                AllowedAmount = l.AllowedAmount,
                PaidAmount = l.PlanPaidAmount,
                PatientResponsibility = l.MemberResponsibility,
                OopAppliedAmount = l.OopAppliedAmount,
                DeductibleCreditedAmount = result.CobPayerSequence is null ? null : l.DeductibleCreditedAmount,
                AdjustmentReasons = MergeAdjustments(
                    MapLineAdjustments(l),
                    i < priorLines.Count ? priorLines[i].AdjustmentReasons : null),
            })
            .ToList();
    }

    /// <summary>
    /// Orders engine line results to match <c>Claim.ClaimLines</c>:
    /// <see cref="PersistenceStage"/> writes line results onto the head
    /// row's claim lines positionally, while the engine emits them sorted
    /// by line number. Falls back to engine order when the two sets don't
    /// correspond one-to-one by line number.
    /// </summary>
    private static IReadOnlyList<LineBenefitResult> OrderByClaimLines(
        AdapterClaim claim,
        IReadOnlyList<LineBenefitResult> engineLines)
    {
        if (claim.ClaimLines.Count != engineLines.Count) return engineLines;

        var byNumber = new Dictionary<int, LineBenefitResult>();
        foreach (var line in engineLines)
        {
            if (!byNumber.TryAdd(line.LineNumber, line)) return engineLines;
        }

        var ordered = new List<LineBenefitResult>(engineLines.Count);
        foreach (var claimLine in claim.ClaimLines)
        {
            if (!byNumber.TryGetValue(claimLine.LineNumber, out var match)) return engineLines;
            ordered.Add(match);
        }
        return ordered;
    }

    /// <summary>
    /// Maps one engine line onto 835-style line CAS. The engine's own
    /// <see cref="LineBenefitResult.Adjustments"/> (CO-45 contractual,
    /// PR-1/2/3 cost share already reduced by any OOP-max cap, a positive
    /// OA-23 COB reduction, CO-denial) are carried verbatim. Two engine shapes need filling in so the line
    /// still balances (charge − ΣCAS = paid) and carries its cost share:
    /// <list type="bullet">
    ///   <item><description>DRG / per-diem lines, whose adjustments live
    ///     on the claim-level <see cref="DrgCostShareResult"/>: PR-1/3/2
    ///     built straight from the line's allocated deductible / copay /
    ///     coinsurance, which the engine has already reduced for any OOP-max
    ///     cap (and which therefore already respect each rule's
    ///     <c>OopApplies</c>). Only a pre-reduction (legacy) shape, whose
    ///     components exceed the member share, gets an ordered reduction —
    ///     see the comment there. A member share above the cost share goes to
    ///     a positive OA-23.</description></item>
    ///   <item><description>Denied lines, which carry only the CO-denial
    ///     against the allowed amount: the billed-over-allowed contractual
    ///     reduction is added as CO-45.</description></item>
    /// </list>
    /// Zero-amount entries are dropped.
    /// </summary>
    internal static List<ClaimAdjustmentReason> MapLineAdjustments(LineBenefitResult line)
    {
        var reasons = new List<ClaimAdjustmentReason>();

        reasons.AddRange(line.Adjustments.Select(a => new ClaimAdjustmentReason
        {
            GroupCode = a.GroupCode,
            ReasonCode = a.ReasonCode,
            RemarkCode = a.RemarkCode,
            Amount = a.Amount,
        }));

        if (line.IsDrgPriced && line.Adjustments.Count == 0)
        {
            // Current engine shape: the components are the reduced amounts
            // the member owes (Σ = allowed − paid). Use them verbatim — no
            // reduction is synthesized, so an OOP-excluded component the
            // engine left whole stays whole.
            var deductible = line.DeductibleAmount;
            var copay = line.CopayAmount;
            var coinsurance = line.CoinsuranceAmount;
            var memberPortion = line.AllowedAmount - line.PlanPaidAmount;
            var excess = deductible + copay + coinsurance - memberPortion;
            if (excess > 0)
            {
                // Legacy shape only (results built before the engine reduced
                // PR amounts for the OOP max: components carry the pre-cap
                // cost share). It is recognisable solely by Σ components
                // exceeding the member share. Forgive in the engine's order —
                // coinsurance, then copay, then deductible — so no CAS is
                // negative. LIMITATION: the line does not say which
                // components had OopApplies=false, so this may reduce an
                // OOP-excluded component the engine would have left whole.
                coinsurance -= Take(coinsurance, ref excess);
                copay -= Take(copay, ref excess);
                deductible -= Take(deductible, ref excess);
            }
            AddIfNonZero(reasons, "PR", "1", deductible);
            AddIfNonZero(reasons, "PR", "3", copay);
            AddIfNonZero(reasons, "PR", "2", coinsurance);
            var residual = memberPortion - (deductible + copay + coinsurance);
            if (residual > 0)
                AddIfNonZero(reasons, "OA", "23", residual);
        }

        if (!reasons.Any(r => r.GroupCode == "CO" && r.ReasonCode == "45"))
        {
            reasons.Insert(0, new ClaimAdjustmentReason
            {
                GroupCode = "CO",
                ReasonCode = "45",
                Amount = line.ContractualAdjustment,
            });
        }

        return reasons.Where(r => r.Amount != 0m).ToList();
    }

    private static decimal Take(decimal amount, ref decimal outstanding)
    {
        var take = Math.Min(Math.Max(amount, 0m), outstanding);
        outstanding -= take;
        return take;
    }

    private static void AddIfNonZero(
        List<ClaimAdjustmentReason> reasons, string group, string carc, decimal amount)
    {
        if (amount == 0m) return;
        reasons.Add(new ClaimAdjustmentReason { GroupCode = group, ReasonCode = carc, Amount = amount });
    }

    /// <summary>
    /// The engine is authoritative for the group/CARC pairs it emits; an
    /// adjustment an earlier stage already wrote on the same line survives
    /// only when the engine didn't emit that pair, so re-deriving cost
    /// share never double counts it.
    /// </summary>
    private static List<ClaimAdjustmentReason> MergeAdjustments(
        List<ClaimAdjustmentReason> engine,
        IReadOnlyList<ClaimAdjustmentReason>? prior)
    {
        if (prior is null || prior.Count == 0) return engine;

        var enginePairs = engine
            .Select(r => (r.GroupCode, r.ReasonCode))
            .ToHashSet();
        return engine
            .Concat(prior.Where(r => !enginePairs.Contains((r.GroupCode, r.ReasonCode))))
            .ToList();
    }

    /// <summary>
    /// Maps the network stage's result onto the engine's cost-share tier:
    /// <list type="bullet">
    ///   <item><description>A matched plan tier
    ///     (<see cref="ClaimAdjudicationContext.MatchedNetworkTier"/>) →
    ///     <see cref="NetworkTier.InNetwork"/>.</description></item>
    ///   <item><description>Membership was evaluated but no tier matched
    ///     (out-of-network, including degraded lookups under
    ///     FailOpen/SoftValidation) → <see cref="NetworkTier.OutOfNetwork"/>.
    ///     Unverified membership is never paid at in-network cost-share.</description></item>
    ///   <item><description>No membership evaluation at all (network stage
    ///     disabled via <c>EnabledStages</c>) →
    ///     <see cref="NetworkTier.InNetwork"/>, the pre-5.6 posture an
    ///     operator opts into by disabling the stage.</description></item>
    /// </list>
    /// </summary>
    internal static NetworkTier ResolveNetworkTier(ClaimAdjudicationContext context)
    {
        if (context.MatchedNetworkTier is not null) return NetworkTier.InNetwork;

        var membershipEvaluated = context.EnforcementOutcomes
            .Any(o => o.Check == EnforcementCheck.Membership);
        return membershipEvaluated ? NetworkTier.OutOfNetwork : NetworkTier.InNetwork;
    }

    private static string NormalizeTier(NetworkTier tier) => tier.ToString();

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
