using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
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

        if (HasUnreconciledRetroactivePlanChange(context.ResolvedMember, claim.ServiceDateFrom, out var pendReason))
        {
            context.PendDetails = new PendDetails
            {
                PendCode = RetroactivePlanChangePendCode,
                PendReason = pendReason,
                PendedAt = DateTime.UtcNow,
            };

            return ClaimAdjudicationStageResult.Pend(StageName, pendReason);
        }

        if (HasUnreviewedSubrogationIndicator(claim, out var subrogationReason))
        {
            context.PendDetails = new PendDetails
            {
                PendCode = SubrogationReviewPendCode,
                PendReason = subrogationReason,
                PendedAt = DateTime.UtcNow,
            };

            return ClaimAdjudicationStageResult.Pend(StageName, subrogationReason);
        }

        if (HasUnmetMedicaidSpendDown(context.ResolvedMember, out var spendDownReason))
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
            LineOfBusiness = (int)claim.LineOfBusiness,
            Member = BuildMemberContext(context.ResolvedMember, claim, serviceDate),
        };
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
        // shift category resolution.
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
            PayerPayment = totals.TotalPlanPaid,
            DenialReasonCode = result.Success ? null : result.DenialReasonCode,
            DenialReason = result.Success ? null : result.DenialReasonDescription,
            AdjustmentReasons = existing.AdjustmentReasons,
            RemarkCodes = existing.RemarkCodes,
            CheckNumber = existing.CheckNumber,
            PaymentDate = existing.PaymentDate,
        };

        context.LineAdjudicationResults = result.Lines
            .Select(l => new ClaimsLineAdj
            {
                AllowedAmount = l.AllowedAmount,
                PaidAmount = l.PlanPaidAmount,
                PatientResponsibility = l.MemberResponsibility,
                OopAppliedAmount = l.OopAppliedAmount,
                AdjustmentReasons = new List<ClaimAdjustmentReason>(),
            })
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
