using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Core Benefit Calculation Engine.
///
/// Given a set of claim lines + member + plan + allowed amounts,
/// computes the complete cost-share breakdown:
///   1. Map each procedure code to a benefit category
///   2. Check coverage (is the service covered? limits exceeded?)
///   3. Apply deductible (from accumulators)
///   4. Apply copay
///   5. Apply coinsurance
///   6. Check OOP max (cap member responsibility)
///   7. Compute plan payment
///   8. Generate CARC/RARC adjustment codes for 835
///
/// This engine is stateless per invocation — accumulator state is
/// fetched at the start and updated at the end. Concurrency is handled
/// via optimistic locking in the accumulator repository.
///
/// QNXT equivalent: The claims adjudication engine's benefit application
/// and cost-sharing modules. In QNXT this is deeply embedded in the
/// adjudication stored procedures; here it's a clean, testable service.
///
/// Design principle: This engine works in both "replace QNXT" mode
/// (using CHO's own benefit configuration) and "augment QNXT" mode
/// (by accepting pre-resolved benefit rules via the IBenefitPlanProvider
/// interface, which can be backed by a QNXT adapter).
/// </summary>
public interface IBenefitCalculationEngine
{
    /// <summary>
    /// Resolve benefits and compute cost sharing for a claim.
    /// </summary>
    Task<BenefitResolutionResult> CalculateAsync(
        BenefitResolutionRequest request,
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
                DenialReasonCode = "16", // CARC 16: Claim/service lacks information
                DenialReasonDescription = "Benefit plan not found"
            };
        }

        // ── Step 2: Load current accumulator state ──
        var planYear = DeterminePlanYear(request.ServiceDate, plan);
        var accumulators = await _accumulatorService.GetAccumulatorsAsync(
            request.MemberId, request.SubscriberId,
            request.BenefitPlanId, planYear, ct);

        // Create a working copy we can mutate as we process lines
        var workingAccumulators = new AccumulatorWorkingSet(accumulators, plan);

        // ── Step 3: Process each line ──
        var lineResults = new List<LineBenefitResult>();

        foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
        {
            var lineResult = await ProcessLineAsync(
                request, line, plan, workingAccumulators, ct);
            lineResults.Add(lineResult);
        }

        // ── Step 4: Compute totals ──
        var totals = ComputeTotals(lineResults);

        // ── Step 5: Persist accumulator updates ──
        var accumulatorSnapshot = workingAccumulators.GetSnapshot();
        await _accumulatorService.ApplyUpdatesAsync(
            request.MemberId, request.SubscriberId,
            request.BenefitPlanId, planYear,
            workingAccumulators.GetPendingUpdates(), ct);

        // ── Step 6: Determine overall claim outcome ──
        var allDenied = lineResults.All(l => !l.IsCovered || l.DenialReasonCode is not null);
        var anyDenied = lineResults.Any(l => !l.IsCovered || l.DenialReasonCode is not null);

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

    /// <summary>
    /// Process a single claim line through the benefit resolution pipeline.
    /// </summary>
    private async Task<LineBenefitResult> ProcessLineAsync(
        BenefitResolutionRequest request,
        ClaimLineInput line,
        BenefitPlanConfig plan,
        AccumulatorWorkingSet accumulators,
        CancellationToken ct)
    {
        var billedAmount = line.BilledAmount;
        var allowedAmount = request.AllowedAmounts.GetValueOrDefault(line.LineNumber, billedAmount);

        // ── Map procedure code to benefit category ──
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

        // ── Look up benefit rules for this category ──
        var benefitCategory = plan.GetCategory(categoryMatch.ServiceTypeCode);
        if (benefitCategory is null)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96", "Non-covered charge(s)",
                $"No benefit configured for service type {categoryMatch.ServiceTypeCode}");
        }

        // ── Check coverage ──
        if (!benefitCategory.IsCovered)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96", "Non-covered charge(s)",
                $"{categoryMatch.ServiceTypeDescription} is not covered under this plan",
                categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription);
        }

        // ── Check visit/day/dollar limits ──
        var limitCheck = CheckLimits(benefitCategory, accumulators, line);
        if (!limitCheck.WithinLimits)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                limitCheck.DenialCode!, limitCheck.DenialDescription!,
                limitCheck.DenialDescription,
                categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription);
        }

        // ── Get cost-sharing rules for the applicable network tier ──
        var costShareRules = request.NetworkTier == NetworkTier.InNetwork
            ? benefitCategory.InNetworkCostSharing
            : benefitCategory.OutOfNetworkCostSharing;

        // ── Apply the cost-sharing waterfall ──
        var result = ApplyCostSharing(
            line, billedAmount, allowedAmount,
            costShareRules, accumulators, request.NetworkTier,
            request.IsEmergency, plan,
            categoryMatch.ServiceTypeCode, categoryMatch.ServiceTypeDescription,
            benefitCategory.AuthRequired);

        // ── Update visit/day counters in accumulators ──
        if (benefitCategory.VisitLimit.HasValue)
        {
            accumulators.IncrementVisitCount(categoryMatch.ServiceTypeCode, (int)line.Units);
        }

        return result;
    }

    /// <summary>
    /// The cost-sharing waterfall — the core financial calculation.
    ///
    /// Order of operations (standard payer adjudication):
    ///   1. Contractual adjustment = Billed - Allowed
    ///   2. Apply deductible (if applicable and not yet met)
    ///   3. Apply copay (flat amount)
    ///   4. Apply coinsurance on remaining allowed after deductible
    ///   5. Check OOP max — if reached, waive remaining member responsibility
    ///   6. Plan pays = Allowed - Member Responsibility
    ///
    /// Special cases:
    ///   - HDHP plans: deductible applies before copay/coinsurance (except preventive)
    ///   - Emergency services: in-network cost sharing applies even for out-of-network
    ///     providers (No Surprises Act / balance billing protections)
    ///   - Copay-only services: some plans waive deductible for certain services
    ///     (e.g., PCP visit copay with no deductible)
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
        // Emergency services: apply in-network cost sharing regardless of network
        // (No Surprises Act, effective 1/1/2022)
        var effectiveNetworkTier = isEmergency ? NetworkTier.InNetwork : networkTier;

        var adjustments = new List<AdjustmentReason>();

        // ── 1. Contractual adjustment (CO-45) ──
        var contractualAdj = Math.Max(0, billedAmount - allowedAmount);
        if (contractualAdj > 0)
        {
            adjustments.Add(new AdjustmentReason
            {
                GroupCode = "CO", // Contractual Obligation
                ReasonCode = "45", // Charges exceed fee schedule/maximum allowable
                Amount = contractualAdj
            });
        }

        // ── 2. Determine what cost-sharing components apply ──
        var deductibleApplies = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Deductible)?.DeductibleApplies ?? false;
        var copayRule = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Copay);
        var coinsuranceRule = costShareRules
            .FirstOrDefault(r => r.CostShareType == CostShareType.Coinsurance);

        var copayAmount = copayRule?.CopayAmount ?? 0;
        var coinsurancePercent = coinsuranceRule?.CoinsurancePercent ?? 0;

        // Remaining allowed to distribute across cost-share components
        var remainingAllowed = allowedAmount;
        decimal deductibleAmount = 0;
        decimal finalCopay = 0;
        decimal coinsuranceAmount = 0;

        // ── 3. Apply deductible ──
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
                    GroupCode = "PR", // Patient Responsibility
                    ReasonCode = "1", // Deductible
                    Amount = deductibleAmount
                });
            }
        }

        // ── 4. Apply copay ──
        if (copayAmount > 0 && remainingAllowed > 0)
        {
            finalCopay = Math.Min(copayAmount, remainingAllowed);
            remainingAllowed -= finalCopay;

            adjustments.Add(new AdjustmentReason
            {
                GroupCode = "PR",
                ReasonCode = "3", // Co-payment
                Amount = finalCopay
            });
        }

        // ── 5. Apply coinsurance on the remainder ──
        if (coinsurancePercent > 0 && remainingAllowed > 0)
        {
            coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);

            if (coinsuranceAmount > 0)
            {
                adjustments.Add(new AdjustmentReason
                {
                    GroupCode = "PR",
                    ReasonCode = "2", // Coinsurance
                    Amount = coinsuranceAmount
                });
            }
        }

        // ── 6. Calculate raw member responsibility ──
        var rawMemberResponsibility = deductibleAmount + finalCopay + coinsuranceAmount;

        // ── 7. Check OOP max — cap member responsibility ──
        decimal oopMaxReduction = 0;
        var oopRemaining = accumulators.GetRemainingOopMax(effectiveNetworkTier);

        if (rawMemberResponsibility > oopRemaining && oopRemaining >= 0)
        {
            // Member has hit or will hit OOP max
            oopMaxReduction = rawMemberResponsibility - oopRemaining;
            rawMemberResponsibility = oopRemaining;

            // Adjust the CAS amounts proportionally (reduce coinsurance first, then copay)
            // In practice: once OOP max is hit, plan pays everything
            if (oopMaxReduction > 0)
            {
                adjustments.Add(new AdjustmentReason
                {
                    GroupCode = "OA", // Other Adjustment
                    ReasonCode = "23", // Impact of prior payer adjudication (OOP max reached)
                    Amount = -oopMaxReduction // Negative = reduces member responsibility
                });
            }
        }

        // Track OOP accumulation
        accumulators.ApplyOopMax(rawMemberResponsibility, effectiveNetworkTier);

        // ── 8. Compute final amounts ──
        var memberResponsibility = rawMemberResponsibility;
        var planPaid = allowedAmount - memberResponsibility;

        return new LineBenefitResult
        {
            LineNumber = line.LineNumber,
            IsCovered = true,
            ServiceTypeCode = serviceTypeCode,
            ServiceTypeDescription = serviceTypeDescription,
            AuthRequired = authRequired,
            AuthFound = true, // Populated by preceding workflow step
            BilledAmount = billedAmount,
            AllowedAmount = allowedAmount,
            ContractualAdjustment = contractualAdj,
            DeductibleAmount = deductibleAmount,
            CopayAmount = finalCopay,
            CoinsuranceAmount = coinsuranceAmount,
            CoinsurancePercent = coinsurancePercent,
            OopMaxReduction = oopMaxReduction,
            MemberResponsibility = memberResponsibility,
            PlanPaidAmount = planPaid,
            Adjustments = adjustments
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════

    private static LimitCheckResult CheckLimits(
        BenefitCategoryConfig category,
        AccumulatorWorkingSet accumulators,
        ClaimLineInput line)
    {
        // Visit limit check
        if (category.VisitLimit.HasValue)
        {
            var used = accumulators.GetVisitCount(category.ServiceTypeCode);
            if (used >= category.VisitLimit.Value)
            {
                return new LimitCheckResult
                {
                    WithinLimits = false,
                    DenialCode = "119", // Benefit maximum for this time period has been reached
                    DenialDescription = $"Visit limit exceeded ({used}/{category.VisitLimit.Value})"
                };
            }
        }

        // Day limit check (for inpatient)
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

        // Dollar limit check
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
        // Most plans use calendar year; some use fiscal year.
        // If plan specifies a year, use it; otherwise derive from service date.
        return plan.PlanYear ?? serviceDate.Year.ToString();
    }

    private record LimitCheckResult
    {
        public bool WithinLimits { get; init; }
        public string? DenialCode { get; init; }
        public string? DenialDescription { get; init; }
    }
}
