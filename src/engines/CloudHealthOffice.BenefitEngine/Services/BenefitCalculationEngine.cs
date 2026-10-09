using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.CobEngine.Services;
using CloudHealthOffice.OperatingMode;
using CobLineInput = CloudHealthOffice.CobEngine.Domain.CobLineInput;
using CobLineResult = CloudHealthOffice.CobEngine.Domain.CobLineResult;
using CobModel = CloudHealthOffice.CobEngine.Domain.CobModel;
using PriorPayerAmount = CloudHealthOffice.CobEngine.Domain.PriorPayerAmount;
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
    /// Calculate benefits with operating mode awareness.
    /// In Replace mode, behaves identically to CalculateAsync.
    /// In Augment mode, also accepts a legacy result for comparison,
    /// logs discrepancies, and returns an AugmentResult wrapping both.
    /// </summary>
    Task<AugmentResult<BenefitResolutionResult>> CalculateWithModeAsync(
        BenefitResolutionRequest request,
        IOperatingMode operatingMode,
        string tenantId,
        BenefitResolutionResult? legacyResult = null,
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
    private readonly IBenefitRuleGate _ruleGate;
    private readonly ILogger<BenefitCalculationEngine> _logger;

    /// <summary>
    /// <see cref="BenefitResolutionResult.PendReasonCode"/> when a per-stay
    /// allocation allows a line more than it billed (pricing review).
    /// </summary>
    public const string AllowedExceedsBilledPendCode = "PRICING";

    /// <summary>Claim type and type of bill for the resolver's institutional fallback.</summary>
    private static ServiceCategoryClaimContext ClaimContext(BenefitResolutionRequest request) =>
        new(request.ClaimType, request.TypeOfBill, request.PlaceOfServiceIsFacilityType);

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public BenefitCalculationEngine(
        IServiceCategoryResolver categoryResolver,
        IBenefitPlanProvider planProvider,
        IAccumulatorService accumulatorService,
        IBenefitRuleGate ruleGate,
        ILogger<BenefitCalculationEngine> logger)
    {
        _categoryResolver = categoryResolver;
        _planProvider = planProvider;
        _accumulatorService = accumulatorService;
        _ruleGate = ruleGate;
        _logger = logger;
    }

    public async Task<BenefitResolutionResult> CalculateAsync(
        BenefitResolutionRequest request,
        CancellationToken ct = default)
    {
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);

        async Task<T> MeasureStageAsync<T>(string stage, Func<Task<T>> action)
        {
            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return await action();
            }
            finally
            {
                stageWatch.Stop();
                timings[stage] = stageWatch.Elapsed.TotalMilliseconds;
            }
        }

        async Task MeasureTaskStageAsync(string stage, Func<Task> action)
        {
            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await action();
            }
            finally
            {
                stageWatch.Stop();
                timings[stage] = stageWatch.Elapsed.TotalMilliseconds;
            }
        }

        T MeasureStage<T>(string stage, Func<T> action)
        {
            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                stageWatch.Stop();
                timings[stage] = stageWatch.Elapsed.TotalMilliseconds;
            }
        }

        _logger.LogInformation(
            "Calculating benefits for member {MemberId}, plan {PlanId}, " +
            "{LineCount} lines, service date {ServiceDate}",
            SanitizeForLog(request.MemberId), request.BenefitPlanId,
            request.Lines.Count, request.ServiceDate);

        // ── Step 1: Load plan configuration ──
        var plan = await MeasureStageAsync(
            "planLookup",
            () => _planProvider.GetPlanAsync(request.BenefitPlanId, ct));
        if (plan is null)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "16",
                DenialReasonDescription = "Benefit plan not found",
                Timings = timings
            };
        }

        // ── Step 2: Load current accumulator state ──
        var planYear = DeterminePlanYear(request.ServiceDate, plan);
        var accumulators = await MeasureStageAsync(
            "accumulatorRead",
            () => _accumulatorService.GetAccumulatorsAsync(
                request.MemberId, request.SubscriberId,
                request.BenefitPlanId, planYear, ct));

        var workingAccumulators = MeasureStage(
            "workingSet",
            () => new AccumulatorWorkingSet(accumulators, plan, _logger));

        // ── Step 3: Check for DRG/per-diem inpatient pricing ──
        var inpatientMethod = DetermineInpatientPricingMethod(request, plan);

        if (inpatientMethod is InpatientPricingMethod.DrgCaseRate or InpatientPricingMethod.PerDiem
            && request.DrgAllowedAmount.HasValue)
        {
            var drgResult = await MeasureStageAsync(
                "drgProcessing",
                () => ProcessDrgClaimAsync(
                    request, plan, workingAccumulators, inpatientMethod, planYear, ct));

            return drgResult with { Timings = timings };
        }

        // ── Step 4: Process each line (standard per-line adjudication) ──
        var lineResults = await MeasureStageAsync("lineProcessing", async () =>
        {
            var results = new List<LineBenefitResult>();

            foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
            {
                var lineResult = await ProcessLineAsync(
                    request, line, plan, workingAccumulators, ct);
                results.Add(lineResult);
            }

            return results;
        });

        // ── Guard: no lines processed → fail fast with a clear denial ──
        if (lineResults.Count == 0)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "16",
                DenialReasonDescription = "Claim submitted with no service lines",
                Timings = timings
            };
        }

        // ── Step 5: Compute totals ──
        var totals = MeasureStage("totals", () => ComputeTotals(lineResults));

        // ── Step 6: Persist accumulator updates ──
        // Prospective (read-only) calculations skip the write entirely so no
        // deductible/OOP/visit/dollar counter is ever mutated. The snapshot
        // is still computed from the in-memory working set so callers can see
        // the projected post-claim balances.
        var accumulatorSnapshot = MeasureStage("snapshot", workingAccumulators.GetSnapshot);
        if (request.ExecutionMode == AdjudicationExecutionMode.Production)
        {
            await MeasureTaskStageAsync(
                "accumulatorWrite",
                () => _accumulatorService.ApplyUpdatesAsync(
                    request.MemberId, request.SubscriberId,
                    request.BenefitPlanId, planYear,
                    request.ClaimId,
                    workingAccumulators.GetPendingUpdates(), ct));
        }

        // ── Step 7: Determine overall claim outcome ──
        var allDenied = MeasureStage(
            "outcome",
            () => lineResults.All(l => !l.IsCovered || l.DenialReasonCode is not null));

        return new BenefitResolutionResult
        {
            Success = !allDenied,
            DenialReasonCode = allDenied ? lineResults.First().DenialReasonCode : null,
            DenialReasonDescription = allDenied ? lineResults.First().DenialReasonDescription : null,
            Lines = lineResults,
            Totals = totals,
            AccumulatorSnapshot = accumulatorSnapshot,
            Timings = timings
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // AUGMENT / REPLACE MODE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AugmentResult<BenefitResolutionResult>> CalculateWithModeAsync(
        BenefitResolutionRequest request,
        IOperatingMode operatingMode,
        string tenantId,
        BenefitResolutionResult? legacyResult = null,
        CancellationToken ct = default)
    {
        var choResult = await CalculateAsync(request, ct);

        if (operatingMode.Mode == EngineOperatingMode.Replace)
        {
            return AugmentResult.ForReplace(choResult);
        }

        // Augment mode: compare with legacy result if available
        var discrepancies = legacyResult is not null
            ? CompareBenefitResults(choResult, legacyResult)
            : Array.Empty<string>();

        return AugmentResult.ForAugment(
            choResult, legacyResult, discrepancies,
            _logger,
            OperatingModeConfiguration.EngineNames.BenefitCalculation,
            tenantId);
    }

    /// <summary>
    /// Compares CHO and legacy benefit results, returning human-readable discrepancy descriptions.
    /// </summary>
    private static string[] CompareBenefitResults(
        BenefitResolutionResult choResult,
        BenefitResolutionResult legacyResult)
    {
        var discrepancies = new List<string>();

        if (choResult.Success != legacyResult.Success)
            discrepancies.Add($"Outcome differs: CHO={choResult.Success}, Legacy={legacyResult.Success}");

        if (choResult.DenialReasonCode != legacyResult.DenialReasonCode)
            discrepancies.Add($"Denial code differs: CHO={choResult.DenialReasonCode ?? "none"}, Legacy={legacyResult.DenialReasonCode ?? "none"}");

        // Compare totals
        if (choResult.Totals.TotalPlanPaid != legacyResult.Totals.TotalPlanPaid)
            discrepancies.Add($"Total plan paid differs: CHO={choResult.Totals.TotalPlanPaid:C}, Legacy={legacyResult.Totals.TotalPlanPaid:C}");

        if (choResult.Totals.TotalMemberResponsibility != legacyResult.Totals.TotalMemberResponsibility)
            discrepancies.Add($"Total member responsibility differs: CHO={choResult.Totals.TotalMemberResponsibility:C}, Legacy={legacyResult.Totals.TotalMemberResponsibility:C}");

        if (choResult.Totals.TotalDeductible != legacyResult.Totals.TotalDeductible)
            discrepancies.Add($"Total deductible differs: CHO={choResult.Totals.TotalDeductible:C}, Legacy={legacyResult.Totals.TotalDeductible:C}");

        if (choResult.Totals.TotalCopay != legacyResult.Totals.TotalCopay)
            discrepancies.Add($"Total copay differs: CHO={choResult.Totals.TotalCopay:C}, Legacy={legacyResult.Totals.TotalCopay:C}");

        if (choResult.Totals.TotalCoinsurance != legacyResult.Totals.TotalCoinsurance)
            discrepancies.Add($"Total coinsurance differs: CHO={choResult.Totals.TotalCoinsurance:C}, Legacy={legacyResult.Totals.TotalCoinsurance:C}");

        if (choResult.Totals.TotalAllowed != legacyResult.Totals.TotalAllowed)
            discrepancies.Add($"Total allowed differs: CHO={choResult.Totals.TotalAllowed:C}, Legacy={legacyResult.Totals.TotalAllowed:C}");

        // Compare line count
        if (choResult.Lines.Count != legacyResult.Lines.Count)
            discrepancies.Add($"Line count differs: CHO={choResult.Lines.Count}, Legacy={legacyResult.Lines.Count}");

        // Compare per-line results by LineNumber (not by index, since ordering may differ)
        var legacyLinesByNumber = legacyResult.Lines.ToDictionary(l => l.LineNumber);
        foreach (var choLine in choResult.Lines)
        {
            if (!legacyLinesByNumber.TryGetValue(choLine.LineNumber, out var legacyLine))
            {
                discrepancies.Add($"Line {choLine.LineNumber} present in CHO but missing from legacy");
                continue;
            }

            if (choLine.PlanPaidAmount != legacyLine.PlanPaidAmount)
                discrepancies.Add($"Line {choLine.LineNumber} plan paid differs: CHO={choLine.PlanPaidAmount:C}, Legacy={legacyLine.PlanPaidAmount:C}");

            if (choLine.IsCovered != legacyLine.IsCovered)
                discrepancies.Add($"Line {choLine.LineNumber} coverage differs: CHO={choLine.IsCovered}, Legacy={legacyLine.IsCovered}");
        }

        // Check for lines present in legacy but not in CHO
        var choLineNumbers = new HashSet<int>(choResult.Lines.Select(l => l.LineNumber));
        foreach (var legacyLine in legacyResult.Lines)
        {
            if (!choLineNumbers.Contains(legacyLine.LineNumber))
                discrepancies.Add($"Line {legacyLine.LineNumber} present in legacy but missing from CHO");
        }

        return discrepancies.ToArray();
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
        var orderedLines = request.Lines.OrderBy(l => l.LineNumber).ToList();
        var allowedByLine = orderedLines
            .Select(l => request.AllowedAmounts.GetValueOrDefault(l.LineNumber, l.BilledAmount))
            .ToList();

        // A line allowed more than it billed (a per-stay rate above total
        // billed, under a contract without a lesser-of-billed provision) needs
        // a negative contractual adjustment that the line-level remittance
        // cannot carry: billed − adjustments would not equal paid. Pend for
        // pricing review — before any cost share is computed or accumulator
        // written — rather than return a result that does not balance.
        var overBilled = orderedLines
            .Select((l, i) => (Line: l, Allowed: allowedByLine[i]))
            .Where(x => x.Allowed > x.Line.BilledAmount)
            .ToList();
        if (overBilled.Count > 0)
        {
            var detail = string.Join(", ", overBilled.Select(x =>
                $"line {x.Line.LineNumber} allowed {x.Allowed:0.00} > billed {x.Line.BilledAmount:0.00}"));
            _logger.LogWarning(
                "Claim {ClaimId}: per-stay allowed exceeds billed on {LineCount} line(s); pending for pricing review",
                SanitizeForLog(request.ClaimId), overBilled.Count);
            return new BenefitResolutionResult
            {
                Success = false,
                RequiresReview = true,
                PendReasonCode = AllowedExceedsBilledPendCode,
                PendReason =
                    $"Per-stay allowed amount {drgAllowed:0.00} allocates more than billed on {detail}; " +
                    "a negative contractual adjustment is not supported — manual pricing review required",
            };
        }

        // Resolve the stay's benefit category from one anchor line (all lines
        // share it): the first room-and-board line (revenue code 0100–0219,
        // the inpatient accommodation) when present, so an ancillary line
        // (pharmacy, lab) listed first cannot pick the stay's benefit;
        // otherwise the first line.
        var firstLine = orderedLines.FirstOrDefault(l => IsAccommodationRevenueCode(l.RevenueCode))
            ?? orderedLines[0];
        var categoryMatch = await _categoryResolver.ResolveAsync(
            plan.TenantId, request.BenefitPlanId, request.ServiceDate,
            firstLine.ProcedureCode, firstLine.CodeType ?? "CPT",
            firstLine.PlaceOfService, firstLine.Modifiers,
            firstLine.RevenueCode, ClaimContext(request), ct);

        if (categoryMatch is null)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                // CARC 204 — same no-mapping condition as the per-line path.
                DenialReasonCode = "204",
                DenialReasonDescription = "No benefit category mapping for DRG claim"
            };
        }

        var gateResult = _ruleGate.PickApplicable(plan, categoryMatch.ServiceTypeCode, request, firstLine);
        if (gateResult.CandidateCount == 0)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "96",
                DenialReasonDescription = $"No benefit configured for service type {categoryMatch.ServiceTypeCode}"
            };
        }

        var benefitCategory = gateResult.Selected;
        if (benefitCategory is null)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "96",
                DenialReasonDescription = $"Benefit category {categoryMatch.ServiceTypeCode} matched but no rule predicate is satisfied for this member encounter"
            };
        }

        if (!benefitCategory.IsCovered)
        {
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "96",
                DenialReasonDescription = $"{benefitCategory.ServiceTypeDescription} is not covered under this plan"
            };
        }

        var effectiveNetworkTier = request.IsEmergency ? NetworkTier.InNetwork : request.NetworkTier;
        var costShareRules = effectiveNetworkTier == NetworkTier.InNetwork
            ? benefitCategory.InNetworkCostSharing
            : benefitCategory.OutOfNetworkCostSharing;

        // Apply cost-sharing waterfall to the DRG allowed amount as a single
        // unit. As secondary payer, COB is applied to the whole stay too: the
        // primary's payment for the stay is the sum of its per-line payments
        // (any key, so a claim-level amount keyed outside the line numbers
        // still counts), measured against total billed and the stay's allowed.
        var drgCostShare = ApplyCostSharingInternal(
            totalBilled, drgAllowed, costShareRules, workingAccumulators,
            effectiveNetworkTier, request.IsEmergency, plan,
            categoryMatch.ServiceTypeCode,
            CobFor(request.Cob, lineNumber: 0, totalBilled, drgAllowed,
                () => PriorPayersForStay(request)));

        // Allocate cost-sharing back to the lines for 835 reporting, in
        // proportion to each line's allowed amount (truncated to the cent,
        // remainder on the last line — see AllocateToLines). The claim-level
        // deductible / copay / coinsurance are already reduced by any OOP-max
        // cap and by COB, so each component sums exactly to its claim-level
        // value, and per line MemberResponsibility = deductible + copay +
        // coinsurance. A COB OA-23 is split the same way, capped per line at
        // allowed − member share so no line pays below zero, and per line
        // PlanPaid = allowed − MemberResponsibility − OA-23: line sums
        // reconcile to the claim totals and every line balances.
        var lines = orderedLines;
        var deductibles = AllocateToLines(drgCostShare.DeductibleApplied, allowedByLine, allowedByLine);
        var afterDeductible = allowedByLine.Select((a, i) => a - deductibles[i]).ToList();
        var copays = AllocateToLines(drgCostShare.CopayApplied, allowedByLine, afterDeductible);
        var afterCopay = afterDeductible.Select((a, i) => a - copays[i]).ToList();
        var coinsurances = AllocateToLines(drgCostShare.CoinsuranceApplied, allowedByLine, afterCopay);
        var members = lines.Select((_, i) => deductibles[i] + copays[i] + coinsurances[i]).ToList();
        // The OOP-counting portion of member responsibility (OopApplies), by
        // the same rule within each line's member share.
        var oopApplied = AllocateToLines(drgCostShare.OopApplied, members, members);
        // Deductible credited to the accumulators: the PR-1 shares when it is
        // the PR-1 total; otherwise (NAIC full credit as a later payer) the
        // credited amount by allowed, by the same rule.
        var deductiblesCredited = drgCostShare.DeductibleCredited == drgCostShare.DeductibleApplied
            ? deductibles
            : AllocateToLines(drgCostShare.DeductibleCredited, allowedByLine, allowedByLine);
        // Informational: the cost share the OOP cap forgave, by allowed.
        var oopReductions = AllocateToLines(drgCostShare.OopMaxReduction, allowedByLine, allowedByLine);
        // COB OA-23 by allowed; Σ caps = stay allowed − member = paid + OA-23,
        // so the caps always hold the full amount.
        var cobOa23s = AllocateToLines(
            drgCostShare.CobOa23, allowedByLine,
            allowedByLine.Select((a, i) => a - members[i]).ToList());

        var lineResults = new List<LineBenefitResult>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineAllowed = allowedByLine[i];
            var contractual = Math.Max(0, line.BilledAmount - lineAllowed);

            var lineAdjustments = new List<AdjustmentReason>();
            if (contractual > 0)
                lineAdjustments.Add(new AdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = contractual });
            if (deductibles[i] > 0)
                lineAdjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = deductibles[i] });
            if (copays[i] > 0)
                lineAdjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "3", Amount = copays[i] });
            if (coinsurances[i] > 0)
                lineAdjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "2", Amount = coinsurances[i] });
            if (cobOa23s[i] > 0)
                lineAdjustments.Add(new AdjustmentReason { GroupCode = "OA", ReasonCode = "23", Amount = cobOa23s[i] });

            lineResults.Add(new LineBenefitResult
            {
                LineNumber = line.LineNumber,
                IsCovered = true,
                ServiceTypeCode = categoryMatch.ServiceTypeCode,
                // Use the picked benefit's description so plans authoring
                // multiple benefits per service-type code (e.g. Pediatric
                // vs Adult Office Visit) report the selected benefit's
                // label rather than the resolver/system label.
                ServiceTypeDescription = benefitCategory.ServiceTypeDescription,
                AuthRequired = benefitCategory.AuthRequired,
                AuthFound = true,
                BilledAmount = line.BilledAmount,
                AllowedAmount = lineAllowed,
                ContractualAdjustment = contractual,
                DeductibleAmount = deductibles[i],
                CopayAmount = copays[i],
                CoinsuranceAmount = coinsurances[i],
                CoinsurancePercent = drgCostShare.CoinsurancePercent,
                OopMaxReduction = oopReductions[i],
                MemberResponsibility = members[i],
                OopAppliedAmount = oopApplied[i],
                DeductibleCreditedAmount = deductiblesCredited[i],
                PlanPaidAmount = lineAllowed - members[i] - cobOa23s[i],
                IsDrgPriced = true,
                // Line-level CAS consistent with the allocated amounts
                // (claim-level detail is on DrgCostShare.Adjustments).
                Adjustments = lineAdjustments
            });
        }

        var totals = ComputeTotals(lineResults);

        // Persist accumulators — skipped for prospective (read-only) estimates.
        var accumulatorSnapshot = workingAccumulators.GetSnapshot();
        if (request.ExecutionMode == AdjudicationExecutionMode.Production)
        {
            await _accumulatorService.ApplyUpdatesAsync(
                request.MemberId, request.SubscriberId,
                request.BenefitPlanId, planYear,
                request.ClaimId,
                workingAccumulators.GetPendingUpdates(), ct);
        }

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
                DeductibleCreditedAmount = drgCostShare.DeductibleCredited,
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
            plan.TenantId, request.BenefitPlanId, request.ServiceDate,
            line.ProcedureCode, line.CodeType ?? "CPT",
            line.PlaceOfService, line.Modifiers,
            line.RevenueCode, ClaimContext(request), ct);

        // CARC 204 (not covered under the patient's current benefit plan):
        // the procedure maps to no benefit category on this plan. 96 is
        // reserved for categories the plan configures but excludes.
        if (categoryMatch is null)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "204", "This service/equipment/drug is not covered under the patient's current benefit plan",
                "No benefit category mapping for procedure code");
        }

        // Capability BP 5.10: route through the rule gate so plans that
        // author multiple benefits with the same ServiceCategory (e.g.
        // pediatric vs adult Office Visit) pick the right one per
        // member encounter. The result distinguishes "no benefit
        // configured" (CandidateCount == 0) from "configured but every
        // predicate rejected" (CandidateCount > 0, Selected == null).
        var gateResult = _ruleGate.PickApplicable(plan, categoryMatch.ServiceTypeCode, request, line);
        if (gateResult.CandidateCount == 0)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96", $"No benefit configured for service type {categoryMatch.ServiceTypeCode}",
                serviceTypeCode: categoryMatch.ServiceTypeCode,
                serviceTypeDescription: categoryMatch.ServiceTypeDescription);
        }

        var benefitCategory = gateResult.Selected;
        if (benefitCategory is null)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96",
                $"Benefit category {categoryMatch.ServiceTypeCode} matched but no rule predicate is satisfied for this member encounter",
                serviceTypeCode: categoryMatch.ServiceTypeCode,
                serviceTypeDescription: categoryMatch.ServiceTypeDescription);
        }

        if (!benefitCategory.IsCovered)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                "96",
                $"{benefitCategory.ServiceTypeDescription} is not covered under this plan",
                serviceTypeCode: categoryMatch.ServiceTypeCode,
                serviceTypeDescription: benefitCategory.ServiceTypeDescription);
        }

        var limitCheck = CheckLimits(benefitCategory, accumulators, line);
        if (!limitCheck.WithinLimits)
        {
            return CreateDeniedLine(line, billedAmount, allowedAmount,
                limitCheck.DenialCode!, limitCheck.DenialDescription!,
                limitCheck.DenialDescription,
                categoryMatch.ServiceTypeCode, benefitCategory.ServiceTypeDescription);
        }

        var networkTierForRules = request.IsEmergency ? NetworkTier.InNetwork : request.NetworkTier;
        var costShareRules = networkTierForRules == NetworkTier.InNetwork
            ? benefitCategory.InNetworkCostSharing
            : benefitCategory.OutOfNetworkCostSharing;

        var result = ApplyCostSharing(
            line, billedAmount, allowedAmount,
            costShareRules, accumulators, request.NetworkTier,
            request.IsEmergency, plan,
            categoryMatch.ServiceTypeCode, benefitCategory.ServiceTypeDescription,
            benefitCategory.AuthRequired,
            CobFor(request.Cob, line.LineNumber, billedAmount, allowedAmount,
                () => PriorPayersForLine(request, line.LineNumber)));

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
        bool authRequired,
        Func<decimal, CobLineResult>? cob = null)
    {
        var effectiveNetworkTier = isEmergency ? NetworkTier.InNetwork : networkTier;

        var costShareResult = ApplyCostSharingInternal(
            billedAmount, allowedAmount, costShareRules, accumulators,
            effectiveNetworkTier, isEmergency, plan, serviceTypeCode, cob);

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
            OopAppliedAmount = costShareResult.OopApplied,
            DeductibleCreditedAmount = costShareResult.DeductibleCredited,
            PlanPaidAmount = costShareResult.PlanPaid,
            Adjustments = costShareResult.Adjustments
        };
    }

    /// <summary>
    /// Shared cost-sharing logic used by both per-line and DRG paths.
    /// <paramref name="cob"/>, when this plan is the secondary payer, maps
    /// the pre-COB (OOP-capped) member responsibility to the CobEngine result
    /// for the same unit (a line, or the whole stay) — see <see cref="CobFor"/>.
    /// </summary>
    private CostShareCalcResult ApplyCostSharingInternal(
        decimal billedAmount,
        decimal allowedAmount,
        IReadOnlyList<CostShareRuleConfig> costShareRules,
        AccumulatorWorkingSet accumulators,
        NetworkTier effectiveNetworkTier,
        bool isEmergency,
        BenefitPlanConfig plan,
        string serviceTypeCode,
        Func<decimal, CobLineResult>? cob = null)
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

        // ── 3b. COB deductible setting: a NoDeductible plan (e.g. Medicaid
        // secondary) neither applies nor credits its deductible when it is
        // not the first payer. Overrides the HDHP deductible-first rule.
        if (cob is not null && plan.CobDeductibleCredit == CobDeductibleCredit.NoDeductible)
            deductibleApplies = false;

        // ── 4. Apply the waterfall based on copay mode ──
        // Amounts only: the deductible accumulator is written after the OOP
        // cap (step 6), with the deductible the member is actually charged.
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
                }
                // Coinsurance on remainder
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                break;

            case CopayApplicationMode.InAdditionToDeductible:
                // Both deductible AND copay apply
                if (deductibleApplies)
                {
                    var deductibleRemaining = accumulators.GetRemainingDeductible(effectiveNetworkTier);
                    deductibleAmount = Math.Max(0, Math.Min(remainingAllowed, deductibleRemaining));
                    remainingAllowed -= deductibleAmount;
                }
                // Copay on top of the deductible
                if (copayAmount > 0)
                {
                    finalCopay = Math.Max(0, Math.Min(copayAmount, remainingAllowed));
                    remainingAllowed -= finalCopay;
                }
                // Coinsurance on remainder
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                break;

            default: // AfterDeductible — standard waterfall
                if (deductibleApplies)
                {
                    var deductibleRemaining = accumulators.GetRemainingDeductible(effectiveNetworkTier);
                    deductibleAmount = Math.Max(0, Math.Min(remainingAllowed, deductibleRemaining));
                    remainingAllowed -= deductibleAmount;
                }
                if (copayAmount > 0 && remainingAllowed > 0)
                {
                    finalCopay = Math.Min(copayAmount, remainingAllowed);
                    remainingAllowed -= finalCopay;
                }
                if (coinsurancePercent > 0 && remainingAllowed > 0)
                    coinsuranceAmount = Math.Round(remainingAllowed * coinsurancePercent, 2);
                break;
        }

        // ── 5. Split by OOP eligibility ──
        // A rule with OopApplies=false contributes cost share that neither
        // consumes nor is capped by the OOP max. Absent rules (e.g. the
        // HDHP-forced deductible) default to counting.
        var deductibleCountsToOop = deductibleRule?.OopApplies ?? true;
        var copayCountsToOop = copayRule?.OopApplies ?? true;
        var coinsuranceCountsToOop = coinsuranceRule?.OopApplies ?? true;

        var oopEligible =
            (deductibleCountsToOop ? deductibleAmount : 0)
            + (copayCountsToOop ? finalCopay : 0)
            + (coinsuranceCountsToOop ? coinsuranceAmount : 0);

        // ── 6. OOP max cap (OOP-eligible portion only) ──
        // When the cap limits cost share, the PR amounts themselves are
        // reduced so they total what the member owes; the 835 carries no
        // negative OA-23. The cap forgives the cost share applied last
        // first — coinsurance, then copay, then deductible — so the
        // deductible the member already satisfied on the way to the cap
        // keeps counting toward the deductible.
        decimal oopMaxReduction = 0;
        if (oopEligible > 0)
        {
            var oopRemaining = accumulators.GetRemainingOopMax(effectiveNetworkTier);

            if (oopEligible > oopRemaining && oopRemaining >= 0)
            {
                oopMaxReduction = oopEligible - oopRemaining;

                var toForgive = oopMaxReduction;
                if (coinsuranceCountsToOop)
                    coinsuranceAmount -= Forgive(coinsuranceAmount, ref toForgive);
                if (copayCountsToOop)
                    finalCopay -= Forgive(finalCopay, ref toForgive);
                if (deductibleCountsToOop)
                    deductibleAmount -= Forgive(deductibleAmount, ref toForgive);
            }
        }

        // ── 7. COB as secondary payer (when the caller supplies it) ──
        // Runs on the OOP-capped cost share, before any accumulator write.
        // CobEngine returns this plan's payment after the primary paid and
        // the member's remaining liability (allowed − primary paid − our
        // payment, never more than the pre-COB cost share — see
        // CobCalculationService). The PR amounts are reduced to that
        // liability in the OOP cap's order — coinsurance, then copay, then
        // deductible — regardless of OopApplies (COB relief is not an OOP
        // event). Everything else between allowed and our payment is the
        // prior payer's impact and goes to a single positive OA-23:
        //   OA-23 = allowed − member liability − secondary payment
        //         = COB savings (pre-COB paid − secondary paid)
        //         + cost share the primary's payment covered,
        // so charge − ΣCAS = paid still holds and no CAS is negative.
        var planPaid = allowedAmount - (deductibleAmount + finalCopay + coinsuranceAmount);
        // The deductible this plan applied as if it were the only plan
        // (after the OOP cap, before COB): what NAIC full credit credits.
        var deductibleBeforeCob = deductibleAmount;
        decimal cobOa23 = 0;
        if (cob is not null)
        {
            var memberBeforeCob = deductibleAmount + finalCopay + coinsuranceAmount;
            var cobResult = cob(memberBeforeCob);

            var toForgive = memberBeforeCob - Math.Min(cobResult.MemberResponsibility, memberBeforeCob);
            coinsuranceAmount -= Forgive(coinsuranceAmount, ref toForgive);
            finalCopay -= Forgive(finalCopay, ref toForgive);
            deductibleAmount -= Forgive(deductibleAmount, ref toForgive);

            planPaid = cobResult.SecondaryPlanPayment;
            cobOa23 = allowedAmount - (deductibleAmount + finalCopay + coinsuranceAmount) - planPaid;
        }

        // ── 8. Accumulators ──
        // OOP: what the member actually owes after the OOP cap and COB — the
        // OOP-counting part of the final PR amounts, recomputed from the
        // components (after the cap alone it equals the OOP remaining). NAIC
        // MDL-120 §7 requires a secondary plan to credit its deductible, not
        // its OOP maximum, and what other plans paid is not the member's
        // out-of-pocket spending, so this holds under every
        // CobDeductibleCredit setting.
        // Deductible: the deductible charged (PR-1) — except that as a later
        // payer under NaicFullCredit (the default) the plan credits the
        // deductible it would have applied with no other coverage
        // (MDL-120 §7: "shall credit to its plan deductible any amounts it
        // would have credited to its deductible in the absence of other
        // health care coverage"), including deductible a prior payer paid.
        // deductibleBeforeCob already respects the remaining deductible
        // (step 4) and the OOP cap (step 6); the working set also keeps the
        // credit within the deductible left in the accumulators. The 835 is
        // unaffected: the credit beyond PR-1 is kept out of the remaining
        // deductible this claim's later lines are priced against
        // (AccumulatorWorkingSet.ApplyDeductibleWithCredit), so it changes
        // the accumulators — and the claims that follow — only.
        oopEligible =
            (deductibleCountsToOop ? deductibleAmount : 0)
            + (copayCountsToOop ? finalCopay : 0)
            + (coinsuranceCountsToOop ? coinsuranceAmount : 0);
        if (oopEligible > 0)
            accumulators.ApplyOopMax(oopEligible, effectiveNetworkTier);
        var deductibleToCredit = cob is not null && plan.CobDeductibleCredit == CobDeductibleCredit.NaicFullCredit
            ? Math.Max(deductibleBeforeCob, deductibleAmount)
            : deductibleAmount;
        var deductibleCredited = accumulators.ApplyDeductibleWithCredit(
            deductibleAmount, deductibleToCredit, effectiveNetworkTier);

        // ── 9. Patient-responsibility CAS (zero entries dropped), COB OA-23 ──
        if (deductibleAmount > 0)
            adjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = deductibleAmount });
        if (finalCopay > 0)
            adjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "3", Amount = finalCopay });
        if (coinsuranceAmount > 0)
            adjustments.Add(new AdjustmentReason { GroupCode = "PR", ReasonCode = "2", Amount = coinsuranceAmount });
        if (cobOa23 > 0)
            adjustments.Add(new AdjustmentReason { GroupCode = "OA", ReasonCode = "23", Amount = cobOa23 });

        var memberResponsibility = deductibleAmount + finalCopay + coinsuranceAmount;

        return new CostShareCalcResult
        {
            ContractualAdj = contractualAdj,
            DeductibleApplied = deductibleAmount,
            CopayApplied = finalCopay,
            CoinsuranceApplied = coinsuranceAmount,
            CoinsurancePercent = coinsurancePercent,
            OopMaxReduction = oopMaxReduction,
            MemberResponsibility = memberResponsibility,
            OopApplied = oopEligible,
            PlanPaid = planPaid,
            CobOa23 = cobOa23,
            DeductibleCredited = deductibleCredited,
            Adjustments = adjustments
        };
    }

    /// <summary>
    /// Takes up to <paramref name="amount"/> off the outstanding OOP-max
    /// reduction and returns how much was taken.
    /// </summary>
    private static decimal Forgive(decimal amount, ref decimal outstanding)
    {
        var take = Math.Min(Math.Max(amount, 0), outstanding);
        outstanding -= take;
        return take;
    }

    // ═══════════════════════════════════════════════════════════════════
    // COB
    // ═══════════════════════════════════════════════════════════════════

    // COB convention (this plan secondary, tertiary or later — PayerSequence
    // ≥ 2), the same on the per-line path and the DRG / per-diem claim-level
    // path:
    //
    //   1. This plan adjudicates as if primary: allowed, deductible, copay,
    //      coinsurance, OOP-max cap (PR amounts already reduced by the cap).
    //   2. Every earlier payer's paid amount and patient responsibility for
    //      the unit come from the claim's 2320/2430 data (line-level 2430
    //      when present, else the claim-level amount prorated by charge —
    //      CobEngine PriorPayerAllocator).
    //   3. CloudHealthOffice.CobEngine applies the requested model —
    //      standard / complementary (pay up to our normal benefit, no more
    //      than allowed − all prior payments, nor more than the last prior
    //      payer's patient responsibility) or non-duplication (pay only what
    //      our normal benefit exceeds all prior payments by). NAIC MDL-120
    //      §6.A(4), §7 — see CobCalculationService.
    //   4. The member owes what is left after all payers, never more than
    //      the pre-COB cost share. PR-1/2/3 are reduced to that amount
    //      (coinsurance, then copay, then deductible — the OOP cap's order),
    //      so ΣPR = MemberResponsibility; zero PR entries are dropped.
    //   5. One positive OA-23 carries the rest of allowed − paid (our COB
    //      savings plus the cost share the prior payers covered — the total
    //      prior-payer reduction, however many payers). No CAS is ever
    //      negative and charge − ΣCAS = paid.
    //   6. The OOP accumulators record the reduced PR amounts — what the
    //      member actually owes. The deductible accumulators follow the
    //      plan's CobDeductibleCredit: NaicFullCredit (default) credits the
    //      pre-COB deductible (MDL-120 §7), MemberPaidOnly the reduced PR-1,
    //      NoDeductible applies and credits none.
    //
    // The DRG path computes all of this once for the stay, then allocates
    // the reduced PR components, the credited deductible and the OA-23 to
    // the lines (truncated to the cent, remainder on the last line).

    private static readonly ICobCalculationService CobCalculator = new CobCalculationService();

    /// <summary>
    /// Builds the COB step for <see cref="ApplyCostSharingInternal"/>, or
    /// null when this plan is the first payer.
    /// <paramref name="priorPayers"/> yields every earlier payer's amounts
    /// for the same unit (one line, or the whole stay).
    /// </summary>
    private static Func<decimal, CobLineResult>? CobFor(
        CobInfo? cob, int lineNumber, decimal billed, decimal allowed,
        Func<IReadOnlyList<PriorPayerAmount>> priorPayers)
    {
        if (cob is null || cob.PayerSequence < 2)
            return null;

        return memberBeforeCob => CobCalculator.Calculate(new CobLineInput
        {
            LineNumber = lineNumber,
            BilledAmount = billed,
            SecondaryAllowedAmount = allowed,
            SecondaryMemberResponsibilityBeforeCob = memberBeforeCob,
            SecondaryPlanPaymentBeforeCob = allowed - memberBeforeCob,
            PriorPayers = priorPayers(),
            Model = cob.UseComplementaryModel ? CobModel.Complementary : CobModel.NonDuplication,
        });
    }

    private static IReadOnlyList<PriorPayerAllocator.ClaimLineCharge> LineCharges(BenefitResolutionRequest request) =>
        request.Lines.Select(l => new PriorPayerAllocator.ClaimLineCharge(l.LineNumber, l.BilledAmount)).ToList();

    /// <summary>
    /// Every prior payer's amounts for one line: from
    /// <see cref="CobInfo.PriorPayers"/> when given, else the legacy
    /// secondary-only <see cref="CobInfo.PrimaryPayerPaymentByLine"/>.
    /// </summary>
    private static IReadOnlyList<PriorPayerAmount> PriorPayersForLine(BenefitResolutionRequest request, int lineNumber)
    {
        var cob = request.Cob!;
        if (cob.PriorPayers.Count == 0)
        {
            return [new PriorPayerAmount
            {
                Sequence = 1,
                PaidAmount = cob.PrimaryPayerPaymentByLine.GetValueOrDefault(lineNumber, 0),
            }];
        }

        var byLine = PriorPayerAllocator.AllocateToLines(LineCharges(request), cob.PriorPayers, cob.PayerSequence);
        return byLine.TryGetValue(lineNumber, out var amounts) ? amounts : [];
    }

    /// <summary>
    /// Every prior payer's amounts for a whole DRG / per-diem stay: each
    /// payer's line amounts summed (the legacy primary map: every value,
    /// any key, so a claim-level amount keyed outside the line numbers
    /// still counts).
    /// </summary>
    private static IReadOnlyList<PriorPayerAmount> PriorPayersForStay(BenefitResolutionRequest request)
    {
        var cob = request.Cob!;
        if (cob.PriorPayers.Count == 0)
        {
            return [new PriorPayerAmount
            {
                Sequence = 1,
                PaidAmount = cob.PrimaryPayerPaymentByLine.Values.Sum(),
            }];
        }

        return PriorPayerAllocator.AllocateToClaim(LineCharges(request), cob.PriorPayers, cob.PayerSequence);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static InpatientPricingMethod DetermineInpatientPricingMethod(
        BenefitResolutionRequest request,
        BenefitPlanConfig plan)
    {
        if (request.ClaimType is not "837I")
            return InpatientPricingMethod.PerLine;

        // The caller priced the stay as one claim-level amount (DRG case rate
        // or all-inclusive per diem): cost share once per stay, regardless of
        // the plan default and of whether a DRG code is present (per diem).
        if (request.InpatientPricingMethod is { } priced and not InpatientPricingMethod.PerLine)
            return priced;

        // Otherwise only institutional claims with DRG info, per plan default.
        if (request.DrgCode is null)
            return InpatientPricingMethod.PerLine;

        return plan.DefaultInpatientPricingMethod;
    }

    /// <summary>UB-04 accommodation (room and board) revenue codes: 0100–0219.</summary>
    private static bool IsAccommodationRevenueCode(string? revenueCode)
    {
        if (string.IsNullOrWhiteSpace(revenueCode)
            || !int.TryParse(revenueCode.Trim(), out var code))
            return false;
        return code is >= 100 and <= 219;
    }

    /// <summary>
    /// Splits a claim-level amount across lines in proportion to
    /// <paramref name="weights"/>: each share is truncated to the cent and
    /// capped at the line's <paramref name="caps"/>; the remainder goes to the
    /// last line, spilling backwards only where the last line has no room
    /// left under its cap. Shares are never negative and always sum exactly
    /// to <paramref name="total"/> (if the caps cannot hold it, the last line
    /// takes the excess). With all weights zero, the split is even.
    /// </summary>
    internal static decimal[] AllocateToLines(
        decimal total, IReadOnlyList<decimal> weights, IReadOnlyList<decimal> caps)
    {
        var count = weights.Count;
        var shares = new decimal[count];
        if (count == 0 || total <= 0)
            return shares;

        var weightSum = weights.Sum(w => Math.Max(w, 0m));
        for (var i = 0; i < count; i++)
        {
            var proportion = weightSum > 0 ? Math.Max(weights[i], 0m) / weightSum : 1m / count;
            var share = Math.Floor(total * proportion * 100m) / 100m;
            shares[i] = Math.Min(share, Math.Max(caps[i], 0m));
        }

        var remainder = total - shares.Sum();
        for (var i = count - 1; i >= 0 && remainder > 0; i--)
        {
            var room = Math.Max(caps[i], 0m) - shares[i];
            if (room <= 0) continue;
            var add = Math.Min(room, remainder);
            shares[i] += add;
            remainder -= add;
        }

        if (remainder > 0)
            shares[count - 1] += remainder;

        return shares;
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
            TotalOopApplied = lines.Sum(l => l.OopAppliedAmount),
            TotalDeductibleCredited = lines.Sum(l => l.DeductibleCreditedAmount),
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
        public decimal OopApplied { get; init; }
        public decimal PlanPaid { get; init; }
        /// <summary>Positive COB OA-23 (0 when not secondary).</summary>
        public decimal CobOa23 { get; init; }
        /// <summary>Deductible credited to the accumulators (see CobDeductibleCredit).</summary>
        public decimal DeductibleCredited { get; init; }
        public List<AdjustmentReason> Adjustments { get; init; } = [];
    }
}
