using ClaimsService.Models;
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
/// Network tier defaults to <see cref="NetworkTier.InNetwork"/> while
/// <see cref="NetworkCredentialingStubStage"/> is in place. Capability 5.6
/// replaces that stub with real network resolution and writes the real
/// tier onto the context before this stage runs (Order 200 → Order 300).
/// </para>
/// </summary>
public sealed class BenefitCalculationStage : IClaimAdjudicationStage
{
    public const string StageName = "BenefitCalculation";

    private readonly IBenefitCalculationEngine _engine;
    private readonly IMemberResolver _memberResolver;
    private readonly ILogger<BenefitCalculationStage> _logger;

    public BenefitCalculationStage(
        IBenefitCalculationEngine engine,
        IMemberResolver memberResolver,
        ILogger<BenefitCalculationStage> logger)
    {
        _engine = engine;
        _memberResolver = memberResolver;
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

        return new BenefitResolutionRequest
        {
            ClaimId = claim.Id,
            MemberId = claim.MemberId,
            SubscriberId = subscriberId,
            BenefitPlanId = planGuid,
            ServiceDate = serviceDate,
            NetworkTier = NetworkTier.InNetwork,
            Lines = claim.ClaimLines.Select(BuildLine).ToList(),
            AllowedAmounts = new Dictionary<int, decimal>(),
            ClaimType = MapClaimType(claim.ClaimType),
            LineOfBusiness = (int)claim.LineOfBusiness,
            Member = BuildMemberContext(context.ResolvedMember, claim),
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

    private static ClaimLineInput BuildLine(AdapterClaimLine line) => new()
    {
        LineNumber = line.LineNumber,
        ProcedureCode = line.ProcedureCode,
        Modifiers = line.Modifiers.ToList(),
        RevenueCode = line.RevenueCode,
        PlaceOfService = line.PlaceOfServiceCode ?? string.Empty,
        BilledAmount = line.ChargeAmount * line.Units,
        Units = line.Units,
        DiagnosisCodes = new List<string>(),
    };

    private static MemberContext? BuildMemberContext(ResolvedMember? member, AdapterClaim claim)
    {
        if (member is null && claim.DiagnosisCodes.Count == 0) return null;

        int? age = null;
        if (member?.DateOfBirth is DateTime dob)
        {
            var today = DateTime.UtcNow.Date;
            age = today.Year - dob.Year;
            if (dob.Date > today.AddYears(-age.Value)) age--;
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
            NetworkTier = NormalizeTier(NetworkTier.InNetwork),
            AllowedAmount = totals.TotalAllowed,
            DeductibleAmount = totals.TotalDeductible,
            CoinsuranceAmount = totals.TotalCoinsurance,
            CopayAmount = totals.TotalCopay,
            PatientResponsibility = totals.TotalMemberResponsibility,
            PayerPayment = totals.TotalPlanPaid,
            DenialReasonCode = result.Success ? existing.DenialReasonCode : result.DenialReasonCode,
            DenialReason = result.Success ? existing.DenialReason : result.DenialReasonDescription,
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
                AdjustmentReasons = new List<ClaimAdjustmentReason>(),
            })
            .ToList();
    }

    private static string NormalizeTier(NetworkTier tier) => tier.ToString();

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
