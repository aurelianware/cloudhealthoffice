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

        // 3. Find the rate line (tries each modifier in order, then base rate)
        FeeScheduleLine? rateLine = null;
        if (schedule is not null)
        {
            rateLine = FindRateLine(schedule, request.ProcedureCode, request.Modifiers);
        }

        // 4. Calculate base allowed amount
        var (baseAmount, rateSource, scheduleType) = CalculateBaseAmount(
            request, schedule, rateLine, networkStatus);

        // 5. Apply modifier adjustments
        var (finalAmount, adjustments) = ApplyModifierAdjustments(
            baseAmount, request, rateLine, schedule);

        // 6. Apply units
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

    public async Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
    {
        var results = new List<PricingResult>(requests.Count);

        foreach (var request in requests.OrderBy(r => r.LineNumber))
            results.Add(await ResolveAsync(request, ct));

        return new PricingResultSet { LineResults = results };
    }

    // ── Schedule selection ─────────────────────────────────────────────

    /// <summary>
    /// Checks contract lines for a procedure-specific override before falling back
    /// to the contract's default FeeScheduleId.
    /// </summary>
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
        if (from is null) return true; // null = all procedures

        var cmp = StringComparer.OrdinalIgnoreCase;

        if (to is null)
            return cmp.Compare(code, from) >= 0;

        return cmp.Compare(code, from) >= 0 && cmp.Compare(code, to) <= 0;
    }

    // ── Rate line lookup ───────────────────────────────────────────────

    /// <summary>
    /// Tries modifiers in claim order, then falls back to base rate (null modifier).
    /// Priority: first exact modifier match wins; then base rate.
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

    // ── Base amount calculation ────────────────────────────────────────

    private (decimal amount, RateSource source, FeeScheduleType scheduleType) CalculateBaseAmount(
        PricingRequest request,
        FeeSchedule? schedule,
        FeeScheduleLine? line,
        NetworkStatus networkStatus)
    {
        if (schedule is null || line is null)
        {
            // UCR fallback — use billed charges
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
                return (line.Rate, RateSource.Drg, FeeScheduleType.Drg);

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
                // If a pre-calculated rate is stored, use it. Otherwise RVU path.
                var amount = line.RateType == FeeScheduleRateType.Rvu
                    ? CalculateRvuAmount(schedule, line, request.PlaceOfServiceCode)
                    : line.Rate;

                if (schedule.PercentOfMedicare.HasValue)
                    amount *= schedule.PercentOfMedicare.Value;

                return (amount, RateSource.Medicaid, FeeScheduleType.Medicaid);
            }

            default: // Commercial, Custom
            {
                var amount = line.RateType switch
                {
                    FeeScheduleRateType.PercentOfBilled  => request.BilledAmount * line.Rate,
                    FeeScheduleRateType.PercentOfMedicare => request.BilledAmount * line.Rate, // caller provides Medicare rate as billed
                    _                                     => line.Rate,
                };

                var source = schedule.Type == FeeScheduleType.Commercial
                    ? RateSource.ContractedRate
                    : RateSource.PlanDefault;

                return (amount, source, schedule.Type);
            }
        }
    }

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
        // Rate line is already the component-specific rate (looked up by modifier).
        // No additional factor needed; record it for the audit trail.
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
            var adj = amount * 0.50m; // extra 50% on top of base
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

        // 51 — multiple procedure reduction (50% for line 2+)
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
