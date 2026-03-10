using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Core Benefit Calculation Engine.
///
/// Cost-sharing waterfall (standard):
///   1. Map procedure code → benefit category
///   2. Check coverage, limits
///   3. Apply deductible
///   4. Apply copay
///   5. Apply coinsurance
///   6. Check OOP max
///   7. Compute plan payment
///   8. Generate CARC/RARC for 835
///
/// Variant behaviors:
///   - HDHP: deductible forced on all services except ACA preventive
///   - CopayInsteadOfDeductible: copay replaces deductible for certain categories
///   - Aggregate family model: single family pool, no individual sub-limits
///   - DRG case rate: cost-sharing applied once per admission, not per line
///   - Reversal: unwind accumulator impact for voided/replaced claims
/// </summary>
public interface IBenefitCalculationEngine
{
    Task<BenefitResolutionResult> CalculateAsync(
        BenefitResolutionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse the accumulator impact of a previously adjudicated claim.
    /// Used for void (CLM05-3=8) and replacement (CLM05-3=7) claims.
    /// </summary>
    Task ReverseClaimAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, DateOnly serviceDate,
        string originalClaimId,
        CancellationToken ct = default);
}

public class BenefitCalculationEngine : IBenefitCalculationEngine
{
    private readonly IServiceCategoryResolver _categoryResolver;
    private readonly IBenefitPlanProvider _planProvider;
    private readonly IAccumulatorService _accumulatorService;
    private readonly ILogger<BenefitCalculationEngine> _logger;

    public BenefitCalculationEngine(
        IServiceCategoryResolver categoryResolver,
        IBenefitPlanProvider planProvider,
        IAccumulatorService accumulatorService,
        ILogger<BenefitCalculationEngine> logger)
    {
        _categoryResolver = categoryResolver;
        _planProvider = planProvider;
        _accumulatorService = accumulatorService;
        _logger = logger;
    }

    public async Task<BenefitResolutionResult> CalculateAsync(
        BenefitResolutionRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Calculating benefits for member {MemberId}, plan {PlanId}, " +
            "{LineCount} lines, service date {ServiceDate}",
            request.MemberId, request.BenefitPlanId,
            request.Lines.Count, request.ServiceDate);

        // ── Step 1: Load plan configuration ──
        var plan = await _planProvider.GetPlanAsync(request.BenefitPlanId, ct);
        if (plan is null)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "16",
                DenialReasonDescription = "Benefit plan not found"
            };
        }

        // ── Step 2: Load current accumulator state ──
        var planYear = DeterminePlanYear(request.ServiceDate, plan);
        var accumulators = await _accumulatorService.GetAccumulatorsAsync(
            request.MemberId, request.SubscriberId,
            request.BenefitPlanId, planYear, ct);

        var workingAccumulators = new AccumulatorWorkingSet(accumulators, plan);

        // ── Step 3: Check for DRG/per-diem inpatient pricing ──
        var inpatientMethod = DetermineInpatientPricingMethod(request, plan);

        if (inpatientMethod is InpatientPricingMethod.DrgCaseRate or InpatientPricingMethod.PerDiem
            && request.DrgAllowedAmount.HasValue)
        {
            return await ProcessDrgClaimAsync(
                request, plan, workingAccumulators, inpatientMethod, planYear, ct);
        }

        // ── Step 4: Process each line (standard per-line adjudication) ──
        var lineResults = new List<LineBenefitResult>();

        foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
        {
            var lineResult = await ProcessLineAsync(
                request, line, plan, workingAccumulators, ct);
            lineResults.Add(lineResult);
        }

        // ── Step 5: Compute totals ──
        var totals = ComputeTotals(lineResults);

        // ── Step 6: Persist accumulator updates ──
        var accumulatorSnapshot = workingAccumulators.GetSnapshot();
        await _accumulatorService.ApplyUpdatesAsync(
            request.MemberId, request.SubscriberId,
            request.BenefitPlanId, planYear,
            request.ClaimId,
            workingAccumulators.GetPendingUpdates(), ct);

        // ── Step 7: Determine overall claim outcome ──
        var allDenied = lineResults.All(l => !l.IsCovered || l.DenialReasonCode is not null);

        return new BenefitResolutionResult
        {
            Success = !allDenied,
            DenialReasonCode = allDenied ? lineResults.First().DenialReasonCode : null,
            DenialReasonDescription = allDenied ? lineResults.First().DenialReasonDescription : null,
            Lines = lineResults,
            Totals = totals,
            AccumulatorSnapshot = accumulatorSnapshot
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // REVERSAL — void / replace claims
    // ═══════════════════════════════════════════════════════════════════

    public async Task ReverseClaimAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, DateOnly serviceDate,
        string originalClaimId,
        CancellationToken ct = default)
    {
        var plan = await _planProvider.GetPlanAsync(benefitPlanId, ct);
        if (plan is null)
        {
            _logger.LogWarning("Cannot reverse claim {ClaimId}: plan {PlanId} not found",
                originalClaimId, benefitPlanId);
            return;
        }

        var planYear = DeterminePlanYear(serviceDate, plan);

        _logger.LogInformation(
            "Reversing accumulators for claim {ClaimId}, member {MemberId}, plan year {PlanYear}",
            originalClaimId, memberId, planYear);

        await _accumulatorService.ReverseAsync(
            memberId, subscriberId, benefitPlanId, planYear, originalClaimId, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DRG / PER-DIEM INPATIENT PROCESSING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Process an inpatient claim using DRG case rate or per-diem pricing.
    /// Cost-sharing is applied once at the claim level, then allocated
    /// proportionally across lines for 835 reporting.
    /// </summary>
    private async Task<BenefitResolutionResult> ProcessDrgClaimAsync(
        BenefitResolutionRequest request,
        BenefitPlanConfig plan,
        AccumulatorWorkingSet workingAccumulators,
        InpatientPricingMethod method,
        string planYear,
        CancellationToken ct)
    {
        var drgAllowed = request.DrgAllowedAmount!.Value;
        var totalBilled = request.Lines.Sum(l => l.BilledAmount);

        // Resolve benefit category from the first line (all lines share the category for DRG)
        var firstLine = request.Lines.OrderBy(l => l.LineNumber).First();
        var categoryMatch = await _categoryResolver.ResolveAsync(
            plan.TenantId, request.BenefitPlanId,
            firstLine.ProcedureCode, firstLine.CodeType ?? "CPT",
            firstLine.PlaceOfService, firstLine.Modifiers,
            firstLine.RevenueCode, ct);

        if (categoryMatch is null)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "18",
                DenialReasonDescription = "No benefit category mapping for DRG claim"
            };
        }

        var benefitCategory = plan.GetCategory(categoryMatch.ServiceTypeCode);
        if (benefitCategory is null || !benefitCategory.IsCovered)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "96",
                DenialReasonDescription = "Service not covered under this plan"
            };
        }

        var effectiveNetworkTier = request.IsEmergency ? NetworkTier.InNetwork : request.NetworkTier;
        var costShareRules = effectiveNetworkTier == NetworkTier.InNetwork
            ? benefitCategory.InNetworkCostSharing
            : benefitCategory.OutOfNetworkCostSharing;

        // Apply cost-sharing waterfall to the DRG allowed amount as a single unit
        var drgCostShare = ApplyCostSharingInternal(
            totalBilled, drgAllowed, costShareRules, workingAccumulators,
            effectiveNetworkTier, request.IsEmergency, plan,
            categoryMatch.ServiceTypeCode);

        // Allocate cost-sharing proportionally across lines for 835 reporting
        var lineResults = new List<LineBenefitResult>();
        foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
        {
            var lineAllowed = request.AllowedAmounts.GetValueOrDefault(line.LineNumber, line.BilledAmount);
            var proportion = drgAllowed > 0 ? lineAllowed / drgAllowed : 0;

            lineResults.Add(new LineBenefitResult
            {
                LineNumber = line.LineNumber,
                IsCovered = true,
                ServiceTypeCode = categoryMatch.ServiceTypeCode,
                ServiceTypeDescription = categoryMatch.ServiceTypeDescription,
                AuthRequired = benefitCategory.AuthRequired,
                AuthFound = true,
                BilledAmount = line.BilledAmount,
                AllowedAmount = lineAllowed,
                ContractualAdjustment = Math.Max(0, line.BilledAmount - lineAllowed),
                DeductibleAmount = Math.Round(drgCostShare.DeductibleApplied * proportion, 2),
                CopayAmount = Math.Round(drgCostShare.CopayApplied * proportion, 2),
                CoinsuranceAmount = Math.Round(drgCostShare.CoinsuranceApplied * proportion, 2),
                CoinsurancePercent = drgCostShare.CoinsurancePercent,
                OopMaxReduction = Math.Round(drgCostShare.OopMaxReduction * proportion, 2),
                MemberResponsibility = Math.Round(drgCostShare.MemberResponsibility * proportion, 2),
                PlanPaidAmount = Math.Round(drgCostShare.PlanPaid * proportion, 2),
                IsDrgPriced = true,
                Adjustments = [] // Adjustments are at the claim level for DRG
            });
        }

        var totals = ComputeTotals(lineResults);

        // Persist accumulators
        var accumulatorSnapshot = workingAccumulators.GetSnapshot();
        await _accumulatorService.ApplyUpdatesAsync(
            request.MemberId, request.SubscriberId,
            request.BenefitPlanId, planYear,
            request.ClaimId,
            workingAccumulators.GetPendingUpdates(), ct);

        return new BenefitResolutionResult
        {
            Success = true,
            Lines = lineResults,
            Totals = totals,
            AccumulatorSnapshot = accumulatorSnapshot,
            DrgCostShare = new DrgCostShareResult
            {
                DrgCode = request.DrgCode,
                DrgAllowedAmount = drgAllowed,
                DeductibleAmount = drgCostShare.DeductibleApplied,
                CopayAmount = drgCostShare.CopayApplied,
                CoinsuranceAmount = drgCostShare.CoinsuranceApplied,
                CoinsurancePercent = drgCostShare.CoinsurancePercent,
                OopMaxReduction = drgCostShare.OopMaxReduction,
                MemberResponsibility = drgCostShare.MemberResponsibility,
                PlanPaidAmount = drgCostShare.PlanPaid,
                Adjustments = drgCostShare.Adjustments
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // PER-LINE PROCESSING
    // ═══════════════════════════════════════════════════════════════════

    private async Task<LineBenefitResult> ProcessLineAsync(
        BenefitResolutionRequest request,
        ClaimLineInput line,
        BenefitPlanConfig plan,
        AccumulatorWorkingSet accumulators,
        CancellationToken ct)
    {
        var billedAmount = line.BilledAmount;
        var allowedAmount = request.AllowedAmounts.GetValueOrDefault(line.LineNumber, billedAmount);

        var categoryMatch = await _categoryResolver.ResolveAsync(
            plan.TenantId, request.BenefitPlanId,
            line.ProcedureCode, line.CodeType ?? "CPT",
            line.PlaceOfService, line.Modifiers,
            line.RevenueCode, ct);

        if (categoryMatch is null)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "18", "Exact duplicate claim/service",
                "No benefit category mapping for procedure code");
        }

        var benefitCategory = plan.GetCategory(categoryMatch.ServiceTypeCode);
        if (benefitCategory is null)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96", "Non-covered charge(s)",
                $"No benefit configured for service type {categoryMatch.ServiceTypeCode}");
        }

        if (!benefitCategory.IsCovered)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96", "Non-covered charge(s)",
                $"{categoryMatch.ServiceTypeDescription} is not covered under this plan",
                categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription);
        }

        var limitCheck = CheckLimits(benefitCategory, accumulators, line);
        if (!limitCheck.WithinLimits)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                limitCheck.DenialCode!, limitCheck.DenialDescription!,
                limitCheck.DenialDescription,
                categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription);
        }

        var networkTierForRules = request.IsEmergency ? NetworkTier.InNetwork : request.NetworkTier;
        var costShareRules = networkTierForRules == NetworkTier.InNetwork
            ? benefitCategory.InNetworkCostSharing
            : benefitCategory.OutOfNetworkCostSharing;

        var result = ApplyCostSharing(
            line, billedAmount, allowedAmount,
            costShareRules, accumulators, request.NetworkTier,
            request.IsEmergency, plan,
            categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription,
            benefitCategory.AuthRequired);

        if (request.Cob is { PayerSequence: 2 } cob)
            result = ApplyCob(result, cob, billedAmount, allowedAmount, line.LineNumber);

        if (benefitCategory.VisitLimit.HasValue)
        {
            accumulators.IncrementVisitCount(categoryMatch.ServiceTypeCode, (int)line.Units);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // COST-SHARING WATERFALL
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The cost-sharing waterfall with full variant support:
    ///
    /// Standard:       Deductible → Copay → Coinsurance → OOP Max
    /// HDHP:           Deductible forced (except exempt) → Copay → Coinsurance → OOP Max
    /// CopayInstead:   Copay (skip deductible) → Coinsurance → OOP Max
    /// CopayInAdd:     Deductible → Copay → Coinsurance → OOP Max (both count)
    /// </summary>
    private LineBenefitResult ApplyCostSharing(
        ClaimLineInput line,
        decimal billedAmount,
        decimal allowedAmount,
        IReadOnlyList<CostShareRuleConfig> costShareRules,
        AccumulatorWorkingSet accumulators,
        NetworkTier networkTier,
        bool isEmergency,
        BenefitPlanConfig plan,
        string serviceTypeCode,
        string serviceTypeDescription,
        bool authRequired)
    {
        var effectiveNetworkTier = isEmergency ? NetworkTier.InNetwork : networkTier;

        var costShareResult = ApplyCostSharingInternal(
            billedAmount, allowedAmount, costShareRules, accumulators,
            effectiveNetworkTier, isEmergency, plan, serviceTypeCode);

        return new LineBenefitResult
        {
            LineNumber = line.LineNumber,
            IsCovered = true,
            ServiceTypeCode = serviceTypeCode,
            ServiceTypeDescription = serviceTypeDescription,
            AuthRequired = authRequired,
            AuthFound = true,
            BilledAmount = billedAmount,
            AllowedAmount = allowedAmount,
            ContractualAdjustment = costShareResult.ContractualAdj,
            DeductibleAmount = costShareResult.DeductibleApplied,
            CopayAmount = costShareResult.CopayApplied,
            CoinsuranceAmount = costShareResult.CoinsuranceApplied,
            CoinsurancePercent = costShareResult.CoinsurancePercent,
            OopMaxReduction = costShareResult.OopMaxReduction,
            MemberResponsibility = costShareResult.MemberResponsibility,
            PlanPaidAmount = costShareResult.PlanPaid,
            Adjustments = costShareResult.Adjustments
        };
    }

    /// <summary>
    /// Shared cost-sharing logic used by both per-line and DRG paths.
    /// </summary>
    private CostShareCalcResult ApplyCostSharingInternal(
        decimal billedAmount,
        decimal allowedAmount,
        IReadOnlyList<CostShareRuleConfig> costShareRules,
        AccumulatorWorkingSet accumulators,
        NetworkTier effectiveNetworkTier,
        bool isEmergency,
        BenefitPlanConfig plan,
        string serviceTypeCode)
    {
        var adjustments = new List<AdjustmentReason>();

        // ── 1. Contractual adjustment (CO-45) ──
        var contractualAdj = Math.Max(0, billedAmount - allowedAmount);
        if (contractualAdj > 0)
        {
            adjustments.Add(new AdjustmentReason
            {
                GroupCode = "CO",
                ReasonCode = "45",
                Amount = contractualAdj
            });
        }

        // ── 2. Resolve cost-sharing rules ──
        var deductibleRule = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Deductible);
        var copayRule = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Copay);
        var coinsuranceRule = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Coinsurance);

        var deductibleApplies = deductibleRule?.DeductibleApplies ?? false;
        var copayAmount = copayRule?.CopayAmount ?? 0;
        var coinsurancePercent = coinsuranceRule?.CoinsurancePercent ?? 0;
        var copayMode = copayRule?.CopayApplicationMode ?? CopayApplicationMode.AfterDeductible;

        // ── 3. HDHP override: force deductible on non-exempt services ──
        if (plan.IsHdhp)
        {
            var isExempt = plan.HdhpDeductibleExemptServices.Contains(serviceTypeCode);
            if (!isExempt)
            {
                // HDHP forces deductible first, regardless of category config
                deductibleApplies = true;
                // HDHP also forces copay after deductible (no "instead of" in HDHP)
                copayMode = CopayApplicationMode.AfterDeductible;
            }
            // Exempt services (preventive): use the category's own rules as-is
        }

        // ── 4. Apply the waterfall based on copay mode ──
        var remainingAllowed = allowedAmount;
        decimal deductibleAmount = 0;
        decimal finalCopay = 0;
        decimal coinsuranceAmount = 0;

        switch (copayMode)
        {
            case CopayApplicationMode.InsteadOfDeductible:
                // Copay replaces deductible — do NOT touch deductible accumulator
                if (copayAmount > 0 && remainingAllowed > 0)
                {
                    finalCopay = Math.Min(copayAmount, remainingAllowed);
                    remainingAllowed -= finalCopay;
                    adjustments.Add(new AdjustmentReason
                    {
                        GroupCode = "PR", ReasonCode = "3", Amount = finalCopay
                    });
                }
                // Coinsurance on remainder
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                {
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                    if (coinsuranceAmount > 0)
                        adjustments.Add(new AdjustmentReason
                        {
                            GroupCode = "PR", ReasonCode = "2", Amount = coinsuranceAmount
                        });
                }
                break;

            case CopayApplicationMode.InAdditionToDeductible:
                // Both deductible AND copay apply
                if (deductibleApplies)
                {
                    var deductibleRemaining = accumulators.GetRemainingDeductible(effectiveNetworkTier);
                    deductibleAmount = Math.Min(remainingAllowed, deductibleRemaining);
                    if (deductibleAmount > 0)
                    {
                        accumulators.ApplyDeductible(deductibleAmount, effectiveNetworkTier);
                        remainingAllowed -= deductibleAmount;
                        adjustments.Add(new AdjustmentReason
                        {
                            GroupCode = "PR", ReasonCode = "1", Amount = deductibleAmount
                        });
                    }
                }
                // Copay on top (does not reduce remaining for coinsurance)
                if (copayAmount > 0)
                {
                    finalCopay = Math.Min(copayAmount, remainingAllowed);
                    remainingAllowed -= finalCopay;
                    adjustments.Add(new AdjustmentReason
                    {
                        GroupCode = "PR", ReasonCode = "3", Amount = finalCopay
                    });
                }
                // Coinsurance on remainder
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                {
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                    if (coinsuranceAmount > 0)
                        adjustments.Add(new AdjustmentReason
                        {
                            GroupCode = "PR", ReasonCode = "2", Amount = coinsuranceAmount
                        });
                }
                break;

            default: // AfterDeductible — standard waterfall
                if (deductibleApplies)
                {
                    var deductibleRemaining = accumulators.GetRemainingDeductible(effectiveNetworkTier);
                    deductibleAmount = Math.Min(remainingAllowed, deductibleRemaining);
                    if (deductibleAmount > 0)
                    {
                        accumulators.ApplyDeductible(deductibleAmount, effectiveNetworkTier);
                        remainingAllowed -= deductibleAmount;
                        adjustments.Add(new AdjustmentReason
                        {
                            GroupCode = "PR", ReasonCode = "1", Amount = deductibleAmount
                        });
                    }
                }
                if (copayAmount > 0 && remainingAllowed > 0)
                {
                    finalCopay = Math.Min(copayAmount, remainingAllowed);
                    remainingAllowed -= finalCopay;
                    adjustments.Add(new AdjustmentReason
                    {
                        GroupCode = "PR", ReasonCode = "3", Amount = finalCopay
                    });
                }
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                {
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                    if (coinsuranceAmount > 0)
                        adjustments.Add(new AdjustmentReason
                        {
                            GroupCode = "PR", ReasonCode = "2", Amount = coinsuranceAmount
                        });
                }
                break;
        }

        // ── 5. Raw member responsibility ──
        var rawMemberResponsibility = deductibleAmount + finalCopay + coinsuranceAmount;

        // ── 6. OOP max cap ──
        decimal oopMaxReduction = 0;
        var oopRemaining = accumulators.GetRemainingOopMax(effectiveNetworkTier);

        if (rawMemberResponsibility > oopRemaining && oopRemaining >= 0)
        {
            oopMaxReduction = rawMemberResponsibility - oopRemaining;
            rawMemberResponsibility = oopRemaining;

            if (oopMaxReduction > 0)
            {
                adjustments.Add(new AdjustmentReason
                {
                    GroupCode = "OA",
                    ReasonCode = "23",
                    Amount = -oopMaxReduction
                });
            }
        }

        accumulators.ApplyOopMax(rawMemberResponsibility, effectiveNetworkTier);

        var memberResponsibility = rawMemberResponsibility;
        var planPaid = allowedAmount - memberResponsibility;

        return new CostShareCalcResult
        {
            ContractualAdj = contractualAdj,
            DeductibleApplied = deductibleAmount,
            CopayApplied = finalCopay,
            CoinsuranceApplied = coinsuranceAmount,
            CoinsurancePercent = coinsurancePercent,
            OopMaxReduction = oopMaxReduction,
            MemberResponsibility = memberResponsibility,
            PlanPaid = planPaid,
            Adjustments = adjustments
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // COB
    // ═══════════════════════════════════════════════════════════════════

    private static LineBenefitResult ApplyCob(
        LineBenefitResult preCob,
        CobInfo cob,
        decimal billed,
        decimal allowed,
        int lineNumber)
    {
        var primaryPay = cob.PrimaryPayerPaymentByLine.GetValueOrDefault(lineNumber, 0);

        decimal secondaryPay;
        decimal cobReduction;

        if (cob.UseComplementaryModel)
        {
            var effectiveBalance = Math.Max(0, billed - primaryPay);
            secondaryPay = Math.Min(preCob.PlanPaidAmount, effectiveBalance);
            cobReduction = preCob.PlanPaidAmount - secondaryPay;
        }
        else
        {
            var maxBenefit = Math.Max(0, allowed - preCob.MemberResponsibility);
            if (primaryPay >= maxBenefit)
            {
                secondaryPay = 0;
                cobReduction = preCob.PlanPaidAmount;
            }
            else
            {
                secondaryPay = maxBenefit - primaryPay;
                cobReduction = preCob.PlanPaidAmount - secondaryPay;
            }
        }

        var memberResp = Math.Max(0, billed - primaryPay - secondaryPay);

        var adjustments = new List<AdjustmentReason>(preCob.Adjustments);
        if (cobReduction > 0)
        {
            adjustments.Add(new AdjustmentReason
            {
                GroupCode = "OA",
                ReasonCode = "23",
                Amount = -cobReduction
            });
        }

        return preCob with
        {
            PlanPaidAmount = secondaryPay,
            MemberResponsibility = memberResp,
            Adjustments = adjustments
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static InpatientPricingMethod DetermineInpatientPricingMethod(
        BenefitResolutionRequest request,
        BenefitPlanConfig plan)
    {
        // Only applies to institutional claims with DRG info
        if (request.ClaimType is not "837I" || request.DrgCode is null)
            return InpatientPricingMethod.PerLine;

        return plan.DefaultInpatientPricingMethod;
    }

    private static LimitCheckResult CheckLimits(
        BenefitCategoryConfig category,
        AccumulatorWorkingSet accumulators,
        ClaimLineInput line)
    {
        if (category.VisitLimit.HasValue)
        {
            var used = accumulators.GetVisitCount(category.ServiceTypeCode);
            if (used >= category.VisitLimit.Value)
            {
                return new LimitCheckResult
                {
                    WithinLimits = false,
                    DenialCode = "119",
                    DenialDescription = $"Visit limit exceeded ({used}/{category.VisitLimit.Value})"
                };
            }
        }

        if (category.DayLimit.HasValue)
        {
            var used = accumulators.GetDayCount(category.ServiceTypeCode);
            if (used >= category.DayLimit.Value)
            {
                return new LimitCheckResult
                {
                    WithinLimits = false,
                    DenialCode = "119",
                    DenialDescription = $"Day limit exceeded ({used}/{category.DayLimit.Value})"
                };
            }
        }

        if (category.DollarLimit.HasValue)
        {
            var used = accumulators.GetDollarAmount(category.ServiceTypeCode);
            if (used >= category.DollarLimit.Value)
            {
                return new LimitCheckResult
                {
                    WithinLimits = false,
                    DenialCode = "119",
                    DenialDescription = $"Dollar limit exceeded (${used}/${category.DollarLimit.Value})"
                };
            }
        }

        return new LimitCheckResult { WithinLimits = true };
    }

    private static LineBenefitResult CreateDeniedLine(
        ClaimLineInput line, decimal billedAmount, decimal allowedAmount,
        string denialCode, string denialDescription, string? detail = null,
        string? serviceTypeCode = null, string? serviceTypeDescription = null)
    {
        return new LineBenefitResult
        {
            LineNumber = line.LineNumber,
            IsCovered = false,
            ServiceTypeCode = serviceTypeCode ?? "Unknown",
            ServiceTypeDescription = serviceTypeDescription ?? "Unknown",
            BilledAmount = billedAmount,
            AllowedAmount = allowedAmount,
            ContractualAdjustment = billedAmount - allowedAmount,
            MemberResponsibility = 0,
            PlanPaidAmount = 0,
            DenialReasonCode = denialCode,
            DenialReasonDescription = denialDescription,
            Adjustments =
            [
                new AdjustmentReason
                {
                    GroupCode = "CO",
                    ReasonCode = denialCode,
                    Amount = allowedAmount
                }
            ]
        };
    }

    private static ClaimTotals ComputeTotals(IReadOnlyList<LineBenefitResult> lines)
    {
        return new ClaimTotals
        {
            TotalBilled = lines.Sum(l => l.BilledAmount),
            TotalAllowed = lines.Sum(l => l.AllowedAmount),
            TotalContractualAdjustment = lines.Sum(l => l.ContractualAdjustment),
            TotalDeductible = lines.Sum(l => l.DeductibleAmount),
            TotalCopay = lines.Sum(l => l.CopayAmount),
            TotalCoinsurance = lines.Sum(l => l.CoinsuranceAmount),
            TotalOopMaxReduction = lines.Sum(l => l.OopMaxReduction),
            TotalMemberResponsibility = lines.Sum(l => l.MemberResponsibility),
            TotalPlanPaid = lines.Sum(l => l.PlanPaidAmount)
        };
    }

    private static string DeterminePlanYear(DateOnly serviceDate, BenefitPlanConfig plan)
    {
        return plan.PlanYear ?? serviceDate.Year.ToString();
    }

    private record LimitCheckResult
    {
        public bool WithinLimits { get; init; }
        public string? DenialCode { get; init; }
        public string? DenialDescription { get; init; }
    }

    /// <summary>
    /// Internal result from the shared cost-sharing calculation.
    /// </summary>
    private record CostShareCalcResult
    {
        public decimal ContractualAdj { get; init; }
        public decimal DeductibleApplied { get; init; }
        public decimal CopayApplied { get; init; }
        public decimal CoinsuranceApplied { get; init; }
        public decimal CoinsurancePercent { get; init; }
        public decimal OopMaxReduction { get; init; }
        public decimal MemberResponsibility { get; init; }
        public decimal PlanPaid { get; init; }
        public List<AdjustmentReason> Adjustments { get; init; } = [];
    }
}