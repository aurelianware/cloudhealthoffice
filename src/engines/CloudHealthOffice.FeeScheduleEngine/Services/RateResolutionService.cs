using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// Core rate resolution engine.
///
/// Thread safety: this service is registered as Scoped. Repository calls are async;
/// no shared mutable state is held between requests.
///
/// Caching: Fee schedules are loaded once per adjudication call and reused across lines
/// (the common case is all lines on a claim sharing one schedule). The caller (adjudication
/// workflow) should cache FeeSchedule objects between claims via its own cache layer.
/// </summary>
public class RateResolutionService : IRateResolutionService
{
    private readonly IFeeScheduleRepository _feeScheduleRepo;
    private readonly IProviderContractRepository _contractRepo;
    private readonly ILogger<RateResolutionService> _logger;

    // Facility POS codes per CMS (11 = office; all others generally treated as facility)
    private static readonly HashSet<string> NonFacilityPosCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "11", // Office
        "12", // Home
        "02", // Telehealth (non-facility)
        "10", // Telehealth (non-facility, home)
    };

    public RateResolutionService(
        IFeeScheduleRepository feeScheduleRepo,
        IProviderContractRepository contractRepo,
        ILogger<RateResolutionService> logger)
    {
        _feeScheduleRepo = feeScheduleRepo;
        _contractRepo = contractRepo;
        _logger = logger;
    }

    public async Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default)
    {
        // 1. Provider contract lookup
        var contract = await _contractRepo.GetContractAsync(
            request.TenantId, request.ProviderNpi, request.PlanId, request.ServiceDate, ct);

        var networkStatus = contract?.NetworkStatus ?? NetworkStatus.Unknown;

        // 2. Determine which fee schedule applies
        var feeScheduleId = ResolveScheduleId(contract, request.ProcedureCode);
        FeeSchedule? schedule = null;

        if (feeScheduleId is not null)
        {
            schedule = await _feeScheduleRepo.GetByIdAsync(request.TenantId, feeScheduleId, ct);
        }

        // If no contracted schedule, fall back to plan default
        if (schedule is null)
        {
            schedule = await _feeScheduleRepo.GetDefaultForPlanAsync(
                request.TenantId, request.PlanId, request.ServiceDate, ct);
        }

        // 3. Find the rate line
        FeeScheduleLine? rateLine = null;
        if (schedule is not null)
        {
            rateLine = schedule.Type == FeeScheduleType.Drg
                ? FindDrgRateLine(schedule, request.DrgCode)
                : FindRateLine(schedule, request.ProcedureCode, request.Modifiers);
        }

        // 4. Calculate base allowed amount
        var (baseAmount, rateSource, scheduleType) = await CalculateBaseAmountAsync(
            request, schedule, rateLine, networkStatus, ct);

        // 5. Apply modifier adjustments (not applicable for DRG/PerDiem/Capitation)
        IReadOnlyList<RateAdjustment> adjustments;
        decimal finalAmount;

        if (scheduleType is FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation)
        {
            // DRG, per diem, and capitation rates are not subject to modifier adjustments
            finalAmount = baseAmount;
            adjustments = [];
        }
        else
        {
            (finalAmount, adjustments) = ApplyModifierAdjustments(
                baseAmount, request, rateLine, schedule);
        }

        // 6. Apply units (not for DRG — case rate is per-admission regardless of line count)
        if (scheduleType != FeeScheduleType.Drg)
            finalAmount *= request.Units;

        return new PricingResult
        {
            LineNumber      = request.LineNumber,
            ProcedureCode   = request.ProcedureCode,
            AllowedAmount   = finalAmount,
            BilledAmount    = request.BilledAmount,
            FeeScheduleType = scheduleType,
            RateSource      = rateSource,
            NetworkStatus   = networkStatus,
            FeeScheduleId   = schedule?.Id,
            FeeScheduleName = schedule?.Name,
            Adjustments     = adjustments,
        };
    }

    /// <summary>
    /// Batch pricing with proper multiple-procedure ranking.
    ///
    /// CMS multiple procedure rules rank lines by allowed amount
    /// (highest-paid = 100%, second = 50%, third+ = 25% for most
    /// endoscopic/surgical families). This implementation:
    ///   1. Prices all lines at 100% first
    ///   2. Ranks by allowed amount descending
    ///   3. Re-applies multiple procedure reductions based on rank
    /// </summary>
    public async Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count <= 1)
        {
            // Single line — no multiple procedure ranking needed
            var results = new List<PricingResult>(requests.Count);
            foreach (var request in requests)
                results.Add(await ResolveAsync(request, ct));
            return new PricingResultSet { LineResults = results };
        }

        // Phase 1: Price all lines at 100% (override LineNumber/TotalLineCount to suppress
        // the per-line multiple procedure logic in ApplyModifierAdjustments)
        var initialResults = new List<(PricingRequest Request, PricingResult Result)>(requests.Count);
        foreach (var request in requests.OrderBy(r => r.LineNumber))
        {
            // Create a modified request that suppresses multiple procedure reduction
            var singleLineRequest = request with { LineNumber = 1, TotalLineCount = 1 };
            var result = await ResolveAsync(singleLineRequest, ct);
            initialResults.Add((request, result));
        }

        // Phase 2: Identify lines eligible for multiple procedure reduction
        var eligibleForReduction = initialResults
            .Where(r => r.Result.FeeScheduleType is not (FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation))
            .OrderByDescending(r => r.Result.AllowedAmount)
            .ToList();

        // Phase 3: Apply rank-based reductions
        var finalResults = new List<PricingResult>(requests.Count);

        foreach (var (request, result) in initialResults)
        {
            // Phase 1 priced each line as a single-line request (LineNumber
            // forced to 1 to suppress per-line MPPR), so restore the original
            // line number here. Without this every batch result carries
            // LineNumber = 1, which collapses/duplicates line identity for any
            // caller that keys results by line number.
            var rankedResult = result with { LineNumber = request.LineNumber };

            var rank = eligibleForReduction.FindIndex(e => e.Request.LineNumber == request.LineNumber);

            if (rank <= 0)
            {
                // Rank 0 (highest paid) or not eligible — no reduction
                finalResults.Add(rankedResult);
                continue;
            }

            // Check if the rate line allows multiple procedure reduction
            // (we need to re-check the rate line's flag)
            var hasMultProcModifier = request.Modifiers.Contains(
                PaymentModifiers.MultipleProcedures, StringComparer.OrdinalIgnoreCase);
            var isMultProcEligible = hasMultProcModifier || eligibleForReduction.Count > 1;

            if (!isMultProcEligible)
            {
                finalResults.Add(rankedResult);
                continue;
            }

            // Rank 1 = 50%, Rank 2+ = 25% (CMS MPPR indicator 2/3 rules)
            var reductionFactor = rank == 1 ? 0.50m : 0.25m;
            var reducedAmount = Math.Round(result.AllowedAmount * reductionFactor, 2);
            var reductionAmount = reducedAmount - result.AllowedAmount;

            var adjustments = new List<RateAdjustment>(result.Adjustments);
            adjustments.Add(new RateAdjustment
            {
                Modifier = PaymentModifiers.MultipleProcedures,
                Description = rank == 1
                    ? $"Multiple procedure reduction — rank {rank + 1} ({reductionFactor:P0} of base)"
                    : $"Multiple procedure reduction — rank {rank + 1} ({reductionFactor:P0} of base)",
                AdjustmentFactor = reductionFactor,
                AdjustmentAmount = reductionAmount,
            });

            finalResults.Add(rankedResult with
            {
                AllowedAmount = reducedAmount,
                Adjustments = adjustments
            });
        }

        return new PricingResultSet
        {
            LineResults = finalResults.OrderBy(r => r.LineNumber).ToList()
        };
    }

    // ── Schedule selection ─────────────────────────────────────────────

    private static string? ResolveScheduleId(ProviderContract? contract, string procedureCode)
    {
        if (contract is null) return null;

        foreach (var line in contract.ContractLines)
        {
            if (IsInCodeRange(procedureCode, line.ProcedureCodeFrom, line.ProcedureCodeTo))
                return line.FeeScheduleId;
        }

        return string.IsNullOrEmpty(contract.FeeScheduleId) ? null : contract.FeeScheduleId;
    }

    private static bool IsInCodeRange(string code, string? from, string? to)
    {
        if (from is null) return true;

        var cmp = StringComparer.OrdinalIgnoreCase;

        if (to is null)
            return cmp.Compare(code, from) >= 0;

        return cmp.Compare(code, from) >= 0 && cmp.Compare(code, to) <= 0;
    }

    // ── Rate line lookup ───────────────────────────────────────────────

    /// <summary>
    /// Procedure code lookup — tries modifiers in claim order, then base rate.
    /// </summary>
    private static FeeScheduleLine? FindRateLine(
        FeeSchedule schedule, string procedureCode, IReadOnlyList<string> modifiers)
    {
        FeeScheduleLine? baseRate = null;

        foreach (var line in schedule.Lines)
        {
            if (!string.Equals(line.ProcedureCode, procedureCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.Modifier is null)
            {
                baseRate = line;
                continue;
            }

            if (modifiers.Any(m => string.Equals(m, line.Modifier, StringComparison.OrdinalIgnoreCase)))
                return line;
        }

        return baseRate;
    }

    /// <summary>
    /// DRG code lookup — matches by DRG code stored in the ProcedureCode field
    /// of the fee schedule line. DRG schedule lines use ProcedureCode to hold
    /// the DRG code (e.g., "470" for major hip/knee joint replacement).
    ///
    /// DRG schedules may include weight-based lines where Rate is the base rate
    /// and the DRG weight is a multiplier. If the line has a DrgWeight, the
    /// allowed amount = Rate × DrgWeight.
    /// </summary>
    private static FeeScheduleLine? FindDrgRateLine(FeeSchedule schedule, string? drgCode)
    {
        if (drgCode is null)
            return null;

        // Exact DRG code match
        var match = schedule.Lines.FirstOrDefault(l =>
            string.Equals(l.ProcedureCode, drgCode, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        // Some DRG schedules use a single "base rate" line (ProcedureCode = "*" or empty)
        // with the DRG weight stored per-line. Check for a wildcard/default line.
        return schedule.Lines.FirstOrDefault(l =>
            string.Equals(l.ProcedureCode, "*", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(l.ProcedureCode));
    }

    // ── Base amount calculation ────────────────────────────────────────

    /// <summary>
    /// Async version of base amount calculation — needed for Medicaid cross-schedule resolution.
    /// </summary>
    private async Task<(decimal amount, RateSource source, FeeScheduleType scheduleType)> CalculateBaseAmountAsync(
        PricingRequest request,
        FeeSchedule? schedule,
        FeeScheduleLine? line,
        NetworkStatus networkStatus,
        CancellationToken ct)
    {
        if (schedule is null || line is null)
        {
            return (request.BilledAmount, RateSource.BilledCharges, FeeScheduleType.Ucr);
        }

        switch (schedule.Type)
        {
            case FeeScheduleType.Capitation:
                return (0m, RateSource.Capitation, FeeScheduleType.Capitation);

            case FeeScheduleType.PerDiem:
            {
                var los = request.LengthOfStay ?? 1;
                var rate = schedule.PerDiemRate ?? line.Rate;
                return (rate * los, RateSource.PerDiem, FeeScheduleType.PerDiem);
            }

            case FeeScheduleType.Drg:
            {
                var drgRate = line.Rate;
                // If DRG weight is specified, rate = base rate × weight
                if (line.DrgWeight.HasValue && line.DrgWeight.Value > 0)
                    drgRate = (schedule.DrgBaseRate ?? line.Rate) * line.DrgWeight.Value;
                return (Math.Round(drgRate, 2), RateSource.Drg, FeeScheduleType.Drg);
            }

            case FeeScheduleType.MedicareMpfs:
            case FeeScheduleType.MedicareOpps:
            {
                var amount = line.RateType == FeeScheduleRateType.Rvu
                    ? CalculateRvuAmount(schedule, line, request.PlaceOfServiceCode)
                    : line.Rate;
                return (amount, RateSource.MedicareMpfs, schedule.Type);
            }

            case FeeScheduleType.Medicaid:
            {
                var amount = await ResolveMedicaidRateAsync(
                    request, schedule, line, ct);
                return (amount, RateSource.Medicaid, FeeScheduleType.Medicaid);
            }

            default: // Commercial, Custom
            {
                var amount = line.RateType switch
                {
                    FeeScheduleRateType.PercentOfBilled   => request.BilledAmount * line.Rate,
                    FeeScheduleRateType.PercentOfMedicare  => request.BilledAmount * line.Rate,
                    _                                      => line.Rate,
                };

                var source = schedule.Type == FeeScheduleType.Commercial
                    ? RateSource.ContractedRate
                    : RateSource.PlanDefault;

                return (amount, source, schedule.Type);
            }
        }
    }

    // ── Medicaid cross-schedule resolution ─────────────────────────────

    /// <summary>
    /// Resolves the Medicaid allowed amount using one of three strategies:
    ///
    /// 1. Pre-calculated flat rate: line.Rate contains the Medicaid rate directly.
    ///    Used when the state publishes a flat fee schedule (most common).
    ///
    /// 2. Percent-of-Medicare with cross-schedule lookup: load the referenced
    ///    Medicare MPFS schedule, calculate the Medicare rate via RVU, then
    ///    apply PercentOfMedicare. Used by states that define Medicaid rates
    ///    as a percentage of Medicare (e.g., "72% of Medicare MPFS").
    ///
    /// 3. Percent-of-Medicare with inline RVU: the Medicaid schedule line
    ///    itself stores RVU values, and the schedule has GPCI/CF and
    ///    PercentOfMedicare. Rate = RVU calculation × PercentOfMedicare.
    ///
    /// QNXT equivalent: FS_FEE_SCHEDULE → REFERENCE_SCHEDULE_ID lookup
    /// for percent-of-Medicare pricing.
    /// </summary>
    private async Task<decimal> ResolveMedicaidRateAsync(
        PricingRequest request,
        FeeSchedule medicaidSchedule,
        FeeScheduleLine medicaidLine,
        CancellationToken ct)
    {
        // Strategy 1: Flat rate (no RVU, no percent-of-Medicare, or rate already pre-calculated)
        if (medicaidLine.RateType == FeeScheduleRateType.FlatRate
            && !medicaidSchedule.PercentOfMedicare.HasValue)
        {
            return medicaidLine.Rate;
        }

        // Strategy 3: Inline RVU on the Medicaid line itself
        if (medicaidLine.RateType == FeeScheduleRateType.Rvu)
        {
            var rvuAmount = CalculateRvuAmount(medicaidSchedule, medicaidLine, request.PlaceOfServiceCode);
            if (medicaidSchedule.PercentOfMedicare.HasValue)
                rvuAmount *= medicaidSchedule.PercentOfMedicare.Value;
            return Math.Round(rvuAmount, 2);
        }

        // Strategy 2: Cross-schedule lookup — load the base Medicare MPFS schedule
        if (medicaidSchedule.BaseMpfsFeeScheduleId is not null
            && medicaidSchedule.PercentOfMedicare.HasValue)
        {
            var baseSchedule = await _feeScheduleRepo.GetByIdAsync(
                request.TenantId, medicaidSchedule.BaseMpfsFeeScheduleId, ct);

            if (baseSchedule is not null)
            {
                var baseLine = FindRateLine(baseSchedule, request.ProcedureCode, request.Modifiers);
                if (baseLine is not null)
                {
                    var medicareRate = baseLine.RateType == FeeScheduleRateType.Rvu
                        ? CalculateRvuAmount(baseSchedule, baseLine, request.PlaceOfServiceCode)
                        : baseLine.Rate;

                    var medicaidRate = medicareRate * medicaidSchedule.PercentOfMedicare.Value;

                    _logger.LogDebug(
                        "Medicaid cross-schedule: {ProcedureCode} Medicare={MedicareRate:C} " +
                        "× {Percent:P0} = {MedicaidRate:C}",
                        request.ProcedureCode, medicareRate,
                        medicaidSchedule.PercentOfMedicare.Value, medicaidRate);

                    return Math.Round(medicaidRate, 2);
                }

                _logger.LogWarning(
                    "Medicaid cross-schedule: base MPFS schedule {ScheduleId} has no line " +
                    "for {ProcedureCode}; falling back to Medicaid line rate",
                    medicaidSchedule.BaseMpfsFeeScheduleId, request.ProcedureCode);
            }
            else
            {
                _logger.LogWarning(
                    "Medicaid cross-schedule: base MPFS schedule {ScheduleId} not found; " +
                    "falling back to Medicaid line rate",
                    medicaidSchedule.BaseMpfsFeeScheduleId);
            }
        }

        // Fallback: use the Medicaid line's stored rate, apply percent if configured
        var fallbackRate = medicaidLine.Rate;
        if (medicaidSchedule.PercentOfMedicare.HasValue)
            fallbackRate *= medicaidSchedule.PercentOfMedicare.Value;

        return Math.Round(fallbackRate, 2);
    }

    // ── RVU calculation ───────────────────────────────────────────────

    private static decimal CalculateRvuAmount(
        FeeSchedule schedule, FeeScheduleLine line, string placeOfServiceCode)
    {
        if (!schedule.ConversionFactor.HasValue)
            return line.Rate; // fall back to stored rate if CF missing

        var isFacility = !NonFacilityPosCodes.Contains(placeOfServiceCode);
        var peRvu = (isFacility ? line.PeRvuFacility : line.PeRvu) ?? line.PeRvu ?? 0m;

        var total = (line.WorkRvu ?? 0m) * schedule.WorkGpci
                  + peRvu                 * schedule.PeGpci
                  + (line.MpRvu ?? 0m)   * schedule.MpGpci;

        return Math.Round(total * schedule.ConversionFactor.Value, 2);
    }

    // ── Modifier adjustments ───────────────────────────────────────────

    private (decimal finalAmount, IReadOnlyList<RateAdjustment> adjustments) ApplyModifierAdjustments(
        decimal baseAmount,
        PricingRequest request,
        FeeScheduleLine? line,
        FeeSchedule? schedule)
    {
        var modifiers = request.Modifiers;
        var adjustments = new List<RateAdjustment>();
        var amount = baseAmount;

        // 26 / TC — professional or technical component
        if (modifiers.Contains(PaymentModifiers.ProfessionalComponent, StringComparer.OrdinalIgnoreCase))
        {
            adjustments.Add(Adjustment(PaymentModifiers.ProfessionalComponent,
                "Professional component only", 1.0m, 0m));
        }
        else if (modifiers.Contains(PaymentModifiers.TechnicalComponent, StringComparer.OrdinalIgnoreCase))
        {
            adjustments.Add(Adjustment(PaymentModifiers.TechnicalComponent,
                "Technical component only", 1.0m, 0m));
        }

        // 50 — bilateral procedure (150%)
        if (modifiers.Contains(PaymentModifiers.Bilateral, StringComparer.OrdinalIgnoreCase)
            && (line?.BilateralAdjustmentApplies ?? true))
        {
            var adj = amount * 0.50m;
            adjustments.Add(Adjustment(PaymentModifiers.Bilateral,
                "Bilateral procedure (150% of unilateral rate)", 1.5m, adj));
            amount += adj;
        }

        // 22 — increased complexity (125%)
        if (modifiers.Contains(PaymentModifiers.IncreasedComplexity, StringComparer.OrdinalIgnoreCase))
        {
            var adj = amount * 0.25m;
            adjustments.Add(Adjustment(PaymentModifiers.IncreasedComplexity,
                "Increased procedural services (125%)", 1.25m, adj));
            amount += adj;
        }

        // 52 / 53 — reduced services or discontinued (50%)
        if (modifiers.Contains(PaymentModifiers.ReducedServices, StringComparer.OrdinalIgnoreCase))
        {
            var adj = amount * -0.50m;
            adjustments.Add(Adjustment(PaymentModifiers.ReducedServices,
                "Reduced services (50% of base rate)", 0.50m, adj));
            amount += adj;
        }
        else if (modifiers.Contains(PaymentModifiers.DiscontinuedProcedure, StringComparer.OrdinalIgnoreCase))
        {
            var adj = amount * -0.50m;
            adjustments.Add(Adjustment(PaymentModifiers.DiscontinuedProcedure,
                "Discontinued procedure (50% of base rate)", 0.50m, adj));
            amount += adj;
        }

        // 62 — co-surgery (62.5% each)
        if (modifiers.Contains(PaymentModifiers.CoSurgery, StringComparer.OrdinalIgnoreCase))
        {
            var reduced = amount * 0.625m;
            var adj = reduced - amount;
            adjustments.Add(Adjustment(PaymentModifiers.CoSurgery,
                "Co-surgery (62.5% of single-surgeon rate)", 0.625m, adj));
            amount = reduced;
        }

        // 80 — assistant surgeon (16%)
        if (modifiers.Contains(PaymentModifiers.AssistantSurgeon, StringComparer.OrdinalIgnoreCase))
        {
            if (line?.AssistantAtSurgeryAllowed ?? true)
            {
                var reduced = amount * 0.16m;
                var adj = reduced - amount;
                adjustments.Add(Adjustment(PaymentModifiers.AssistantSurgeon,
                    "Assistant surgeon (16% of primary rate)", 0.16m, adj));
                amount = reduced;
            }
            else
            {
                var adj = -amount;
                adjustments.Add(Adjustment(PaymentModifiers.AssistantSurgeon,
                    "Assistant surgeon not allowed for this procedure ($0)", 0m, adj));
                amount = 0m;
            }
        }

        // AS — assistant-at-surgery (85% of assistant surgeon rate = 85% × 16% = 13.6%)
        if (modifiers.Contains(PaymentModifiers.AssistantAtSurgery, StringComparer.OrdinalIgnoreCase))
        {
            if (line?.AssistantAtSurgeryAllowed ?? true)
            {
                var assistantBase = amount * 0.16m;
                var reduced = assistantBase * 0.85m;
                var adj = reduced - amount;
                adjustments.Add(Adjustment(PaymentModifiers.AssistantAtSurgery,
                    "Assistant-at-surgery (85% of assistant rate)", 0.85m, adj));
                amount = reduced;
            }
            else
            {
                var adj = -amount;
                adjustments.Add(Adjustment(PaymentModifiers.AssistantAtSurgery,
                    "Assistant-at-surgery not allowed for this procedure ($0)", 0m, adj));
                amount = 0m;
            }
        }

        // Note: Multiple procedure reduction (mod 51) is now handled in ResolveBatchAsync
        // via rank-based ordering. The per-line fallback below only applies when
        // ResolveBatchAsync is not used (single-line ResolveAsync calls).
        if (modifiers.Contains(PaymentModifiers.MultipleProcedures, StringComparer.OrdinalIgnoreCase)
            || (request.LineNumber > 1 && request.TotalLineCount > 1
                && (line?.MultipleProcedureReductionApplies ?? true)))
        {
            var reduced = amount * 0.50m;
            var adj = reduced - amount;
            adjustments.Add(Adjustment(PaymentModifiers.MultipleProcedures,
                "Multiple procedure reduction (50% for secondary procedures)", 0.50m, adj));
            amount = reduced;
        }

        return (Math.Max(amount, 0m), adjustments);
    }

    private static RateAdjustment Adjustment(
        string modifier, string description, decimal factor, decimal dollarAmount)
        => new()
        {
            Modifier          = modifier,
            Description       = description,
            AdjustmentFactor  = factor,
            AdjustmentAmount  = dollarAmount,
        };
}
