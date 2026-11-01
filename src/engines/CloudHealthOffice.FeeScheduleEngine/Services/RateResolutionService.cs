using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.ReferenceData.Domain;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Money;

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

    private static readonly string[] AssistantSurgeonModifiers =
    [
        PaymentModifiers.AssistantSurgeon,
        PaymentModifiers.MinimumAssistantSurgeon,
        PaymentModifiers.AssistantSurgeonNoQualifiedResident,
    ];

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
        var line = await ResolveLineAsync(
            request,
            applyMultipleProcedureReduction: true,
            multipleProcedureContext: request.TotalLineCount > 1,
            ct);

        // A single line is its own claim: a per-stay amount is compared with the
        // line's billed charge, which is then the stay's total billed charge.
        return line.LesserOfBilled ? ApplyLesserOfBilled(line.Result) : line.Result;
    }

    /// <summary>
    /// The engine's facility / non-facility rule: the CMS MPFS site-of-service
    /// differential defined once in <see cref="FacilityPlaceOfService"/> (facility
    /// rate only for POS 02, 19, 21–24, 26, 31, 34, 41, 42, 51–53, 56, 61).
    /// It selects facility PE RVUs and <see cref="FeeScheduleLine.FacilityRate"/>.
    /// Public so callers that display the setting (PricingApi) use the same rule.
    /// </summary>
    public static bool IsFacilityPlaceOfService(string? placeOfServiceCode)
        => FacilityPlaceOfService.IsFacility(placeOfServiceCode);

    /// <summary>
    /// Whether a claim line takes the facility rate. An institutional line — the
    /// claim type is institutional (<see cref="PricingRequest.IsInstitutional"/>) or it
    /// carries a valid <see cref="PricingRequest.BillType"/> — is billed by a facility
    /// and is always a facility setting: its <see cref="PricingRequest.PlaceOfServiceCode"/>
    /// then holds the 837I CLM05-1 facility type code ("13" = hospital outpatient),
    /// which is not a CMS place of service and is never read against the POS list.
    /// A malformed bill type ("0", "N/A") does not make a line institutional; the
    /// validity rule is <see cref="NubcTypeOfBill"/>, shared with the benefit engine.
    /// A professional line uses <see cref="IsFacilityPlaceOfService"/>.
    /// Public so callers that display the setting (PricingApi) use the same rule.
    /// </summary>
    public static bool IsFacilitySetting(PricingRequest request)
        => request.IsInstitutional
           || NubcTypeOfBill.IsValid(request.BillType)
           || IsFacilityPlaceOfService(request.PlaceOfServiceCode);

    /// <summary>
    /// Per-line resolution output for batch pricing: the result, the matched
    /// rate line, and whether the amount is a per-stay (claim-level) rate —
    /// a DRG case rate or an all-inclusive per diem — that must be paid once
    /// per claim rather than on every line.
    /// </summary>
    private readonly record struct LineResolution(
        PricingResult Result, FeeScheduleLine? RateLine, bool IsPerStay, bool LesserOfBilled,
        bool MultipleProcedureReduction = true);

    private static bool HasPlanCode(PricingRequest request) =>
        !string.IsNullOrWhiteSpace(request.PlanCode)
        && !string.Equals(request.PlanCode, request.PlanId, StringComparison.Ordinal);

    /// <summary>
    /// Prices one line and also returns the matched rate line so batch pricing can
    /// honour per-line flags (e.g. <see cref="FeeScheduleLine.MultipleProcedureReductionApplies"/>).
    /// When <paramref name="applyMultipleProcedureReduction"/> is false the per-line
    /// modifier-51 / line-position reduction is suppressed (batch pricing ranks instead).
    /// When <paramref name="multipleProcedureContext"/> is true (the claim has more than
    /// one line) the result is flagged if the rate line's multiple procedure indicator
    /// is missing or names a reduction rule the engine does not implement.
    /// </summary>
    private async Task<LineResolution> ResolveLineAsync(
        PricingRequest request, bool applyMultipleProcedureReduction, bool multipleProcedureContext,
        CancellationToken ct)
    {
        // 1. Provider contract lookup
        // A contract for this plan version wins; else one for the plan code,
        // which follows the plan across amendments.
        var contract = await _contractRepo.GetContractAsync(
            request.TenantId, request.ProviderNpi, request.PlanId, request.ServiceDate, ct);
        if (contract is null && HasPlanCode(request))
            contract = await _contractRepo.GetContractAsync(
                request.TenantId, request.ProviderNpi, request.PlanCode!, request.ServiceDate, ct);

        var networkStatus = contract?.NetworkStatus ?? NetworkStatus.Unknown;

        // 2. The contract rule that prices this line: the first reimbursement
        //    term whose scope and dates match it, or — for a contract with no
        //    terms — its legacy FeeScheduleId and ContractLines (ADR 016 §2).
        var term = ResolveContractTerm(contract, request);
        var lesserOfBilled = term.LesserOfBilled ?? contract?.LesserOfBilledCharges ?? false;

        // A term that matched but cannot be priced (a provision the engine
        // stores without evaluating, or a term missing the field its method
        // needs) pends the line. It must not fall through to the plan default,
        // which would quietly pay a rate the contract does not name.
        if (term.PendReason is not null)
            return TermUnpriceable(request, networkStatus, term);

        // Capitated and percent-of-billed terms read no fee schedule.
        if (term.Method == ReimbursementMethod.Capitation)
            return CapitatedLine(request, networkStatus, term);

        if (term.Method == ReimbursementMethod.PercentOfBilled)
            return PercentOfBilledLine(
                request, networkStatus, term, lesserOfBilled, applyMultipleProcedureReduction);

        var feeScheduleId = term.FeeScheduleId;
        FeeSchedule? schedule = null;

        if (feeScheduleId is not null)
        {
            schedule = await _feeScheduleRepo.GetByIdAsync(request.TenantId, feeScheduleId, ct);
        }

        // A reimbursement term that names a fee schedule the store does not hold
        // is a broken contract, not an absent one: pend the line rather than pay
        // it from the plan default, which is not the rate the contract names.
        if (schedule is null && feeScheduleId is not null && term.Method is not null)
        {
            return TermUnpriceable(request, networkStatus, term with
            {
                PendReason = $"contract term names fee schedule {feeScheduleId}, which this tenant has no " +
                             "schedule for; the line is pended rather than priced from another schedule",
            });
        }

        // If no contracted schedule, fall back to plan default
        if (schedule is null)
        {
            schedule = await _feeScheduleRepo.GetDefaultForPlanAsync(
                request.TenantId, request.PlanId, request.ServiceDate, ct);
            if (schedule is null && HasPlanCode(request))
                schedule = await _feeScheduleRepo.GetDefaultForPlanAsync(
                    request.TenantId, request.PlanCode!, request.ServiceDate, ct);
        }

        // 3. Find the rate line
        FeeScheduleLine? rateLine = null;
        if (schedule is not null)
        {
            rateLine = schedule.Type == FeeScheduleType.Drg
                ? FindDrgRateLine(schedule, request.DrgCode)
                : FindRateLine(schedule, request.ProcedureCode, request.Modifiers, request.RevenueCode);

            // An all-inclusive per diem covers every service on an inpatient
            // stay: a line with no specific rate line (pharmacy, lab, supplies)
            // still prices under the schedule's daily rate — once per claim,
            // see ResolveBatchAsync. Only for stays (LengthOfStay supplied), so
            // a per-diem schedule never captures an unrelated outpatient line.
            if (rateLine is null
                && schedule is { Type: FeeScheduleType.PerDiem, PerDiemRate: not null }
                && request.LengthOfStay is not null)
            {
                rateLine = new FeeScheduleLine
                {
                    ProcedureCode = request.ProcedureCode,
                    RevenueCode = request.RevenueCode,
                    RateType = FeeScheduleRateType.FlatRate,
                    Rate = schedule.PerDiemRate.Value,
                    MultipleProcedureIndicator = MultipleProcedureIndicator.NotApplicable,
                };
            }
        }

        // 4. Calculate base allowed amount
        var (baseAmount, rateSource, scheduleType, unresolvedReason, isLineTotal) = await CalculateBaseAmountAsync(
            request, schedule, rateLine, networkStatus, ct);

        if (unresolvedReason is not null)
        {
            // A rate line matched but its base could not be computed (e.g. percent-of-Medicare
            // with no resolvable Medicare reference). Report it as unresolved so the caller can
            // pend the line, rather than silently substituting billed charges.
            _logger.LogWarning(
                "Rate unresolved for {ProcedureCode} on schedule {ScheduleId}: {Reason}",
                LogSanitizer.Sanitize(request.ProcedureCode, 256), LogSanitizer.Sanitize(schedule?.Id, 256),
                LogSanitizer.Sanitize(unresolvedReason, 256));

            return new LineResolution(new PricingResult
            {
                LineNumber       = request.LineNumber,
                ProcedureCode    = request.ProcedureCode,
                AllowedAmount    = 0m,
                BilledAmount     = request.BilledAmount,
                FeeScheduleType  = scheduleType,
                RateSource       = RateSource.Unresolved,
                NetworkStatus    = networkStatus,
                FeeScheduleId    = schedule?.Id,
                FeeScheduleName  = schedule?.Name,
                UnresolvedReason = unresolvedReason,
            }, rateLine, IsPerStay: false, LesserOfBilled: false);
        }

        // 4a. A term's multiplier scales the rate the schedule produced — the
        //     FfsRateConfig percentage of a fee schedule, or the percentage of
        //     the Medicare allowed amount — before modifiers, units, the
        //     multiple procedure reduction and lesser-of. A legacy contract and
        //     a term that pays the schedule's own rate both carry 1.0, which
        //     leaves the amount and the adjustment list untouched.
        var termAdjustments = new List<RateAdjustment>();
        if (term.RateMultiplier != 1.0m)
        {
            var scheduleAmount = baseAmount;
            baseAmount = MoneyRounding.ToCents(scheduleAmount * term.RateMultiplier);
            termAdjustments.Add(new RateAdjustment
            {
                Modifier = string.Empty,
                Description = $"{term.Describe()}: {term.RateMultiplier:P2} of the {scheduleType} rate {scheduleAmount:0.00}",
                AdjustmentFactor = term.RateMultiplier,
                AdjustmentAmount = baseAmount - scheduleAmount,
            });
        }

        // 5. Apply modifier adjustments (not applicable for DRG/PerDiem/Capitation)
        IReadOnlyList<RateAdjustment> adjustments;
        decimal finalAmount;

        if (scheduleType is FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation)
        {
            // DRG, per diem, and capitation rates are not subject to modifier adjustments
            finalAmount = baseAmount;
            adjustments = termAdjustments;
        }
        else
        {
            (finalAmount, adjustments) = ApplyModifierAdjustments(
                baseAmount, request, rateLine,
                applyMultipleProcedureReduction && term.MultipleProcedureReduction,
                term.BilateralAdjustment);

            if (termAdjustments.Count > 0)
                adjustments = [.. termAdjustments, .. adjustments];
        }

        // 6. Apply units (not for DRG — case rate is per-admission regardless of line count —
        //    nor for line-total amounts: billed-based amounts, since BilledAmount is the line
        //    total (837 SV102/SV203), and per diem × length of stay, which already counts days)
        if (scheduleType != FeeScheduleType.Drg && !isLineTotal)
            finalAmount *= request.Units;

        // Allowed amounts are money: rounded to cents once, here, so every caller
        // (adjudication, estimates, the Pricing API) sees the same figure.
        finalAmount = MoneyRounding.ToCents(finalAmount);

        // Per-stay amounts are paid once per claim (ResolveBatchAsync): a DRG case
        // rate, or an all-inclusive per diem priced for the length of stay.
        var isPerStay = rateSource == RateSource.Drg
            || (rateSource == RateSource.PerDiem && schedule?.PerDiemRate is not null && request.LengthOfStay is not null);

        // 7. Flag lines whose multiple procedure treatment could not be determined,
        //    and a term whose method does not match the schedule it names.
        var warnings = multipleProcedureContext
            ? MultipleProcedureIndicatorWarnings(request, rateLine, rateSource, scheduleType)
            : [];

        if (TermMethodWarning(term, schedule) is { } methodWarning)
            warnings = [.. warnings, methodWarning];

        return new LineResolution(new PricingResult
        {
            LineNumber      = request.LineNumber,
            ProcedureCode   = request.ProcedureCode,
            AllowedAmount   = finalAmount,
            BaseAmount      = baseAmount,
            BilledAmount    = request.BilledAmount,
            FeeScheduleType = scheduleType,
            RateSource      = rateSource,
            NetworkStatus   = networkStatus,
            FeeScheduleId   = schedule?.Id,
            FeeScheduleName = schedule?.Name,
            Adjustments     = adjustments,
            IsPerStayRate   = isPerStay,
            Warnings        = warnings,
        }, rateLine, isPerStay, lesserOfBilled, term.MultipleProcedureReduction);
    }

    /// <summary>
    /// Batch pricing with proper multiple-procedure ranking and once-per-claim
    /// per-stay rates.
    ///
    /// CMS multiple surgery rules (MPFS multiple procedure indicator 2) rank
    /// eligible procedures by allowed amount: highest = 100%, 2nd through 5th
    /// = 50%. 6th and subsequent are "by report"; this engine prices them at
    /// 50% and flags the adjustment for review. This implementation:
    ///   1. Prices all lines at 100% (per-line multiple procedure logic suppressed)
    ///   2. Ranks only lines whose rate line carries indicator 2
    ///      (<see cref="FeeScheduleLine.MultipleProcedureReductionApplies"/>);
    ///      E&amp;M (0), not-applicable (9) and lines with no indicator are left
    ///      unreduced. Indicators 3–7 (endoscopy, imaging, therapy, cardiovascular,
    ///      ophthalmology) follow different CMS rules that are not yet implemented,
    ///      so those lines are left unreduced and flagged in
    ///      <see cref="PricingResult.Warnings"/>, as are lines with no indicator.
    ///   3. Applies the rank-based reduction to ranked lines 2+
    ///   4. Applies the contract's lesser-of-billed provision, when it has one,
    ///      to each ordinary line after every other adjustment.
    ///   5. Pays a per-stay rate (DRG case rate, all-inclusive per diem) once
    ///      per claim, allocated across the per-stay lines in proportion to
    ///      their billed charges — see <see cref="AllocatePerStayAmounts"/>.
    ///      Without this, an N-line inpatient claim would be paid N case rates.
    ///      Per-stay lines priced from more than one schedule are marked
    ///      unresolved (the claim pends) rather than stacking stay rates.
    ///
    /// A batch is one claim's lines — the same assumption the multiple
    /// procedure ranking already makes.
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

        // Phase 1: Price all lines at 100% (per-line multiple procedure reduction suppressed)
        var initialResults = new List<LineResolution>(requests.Count);
        foreach (var request in requests.OrderBy(r => r.LineNumber))
            initialResults.Add(await ResolveLineAsync(
                request, applyMultipleProcedureReduction: false, multipleProcedureContext: true, ct));

        // Phase 2: Rank the lines eligible for multiple procedure reduction
        var rankByIndex = Enumerable.Range(0, initialResults.Count)
            .Where(i => IsMultipleProcedureEligible(initialResults[i]))
            .OrderByDescending(i => initialResults[i].Result.AllowedAmount)
            .ThenBy(i => initialResults[i].Result.LineNumber)
            .Select((index, rank) => (index, rank))
            .ToDictionary(x => x.index, x => x.rank);

        // Phase 3: Apply rank-based reductions
        var finalResults = new List<PricingResult>(requests.Count);

        for (var i = 0; i < initialResults.Count; i++)
        {
            var result = initialResults[i].Result;

            if (!rankByIndex.TryGetValue(i, out var rank) || rank == 0)
            {
                // Highest-ranked eligible line, or not eligible — no reduction
                finalResults.Add(result);
                continue;
            }

            // Rank 2–5 = 50%; rank 6+ is "by report" under CMS — priced at 50% and flagged
            const decimal reductionFactor = 0.50m;
            var byReport = rank >= 5;
            var reducedAmount = MoneyRounding.ToCents(result.AllowedAmount * reductionFactor);
            var reductionAmount = reducedAmount - result.AllowedAmount;

            if (byReport)
            {
                _logger.LogWarning(
                    "Multiple procedure rank {Rank} for line {LineNumber} ({ProcedureCode}) is by report; " +
                    "priced at {Factor:P0} pending review",
                    rank + 1, result.LineNumber, LogSanitizer.Sanitize(result.ProcedureCode, 256), reductionFactor);
            }

            var adjustments = new List<RateAdjustment>(result.Adjustments);
            adjustments.Add(new RateAdjustment
            {
                Modifier = PaymentModifiers.MultipleProcedures,
                Description = byReport
                    ? $"Multiple procedure reduction — rank {rank + 1} is by report; priced at {reductionFactor:P0} of base, review required"
                    : $"Multiple procedure reduction — rank {rank + 1} ({reductionFactor:P0} of base)",
                AdjustmentFactor = reductionFactor,
                AdjustmentAmount = reductionAmount,
            });

            finalResults.Add(result with
            {
                AllowedAmount = reducedAmount,
                Adjustments = adjustments
            });
        }

        // Phase 4: Lesser-of-billed on each ordinary line, after every contract
        // adjustment (per-stay lines are compared at the stay level in phase 5)
        for (var i = 0; i < initialResults.Count; i++)
        {
            if (initialResults[i].LesserOfBilled && !initialResults[i].IsPerStay)
                finalResults[i] = ApplyLesserOfBilled(finalResults[i]);
        }

        // Phase 5: Per-stay rates are paid once per claim, allocated across the lines
        AllocatePerStayAmounts(initialResults, finalResults);

        return new PricingResultSet
        {
            LineResults = finalResults.OrderBy(r => r.LineNumber).ToList()
        };
    }

    /// <summary>
    /// Every line of a DRG or all-inclusive per-diem claim prices to the same
    /// claim-level amount (the case rate, or per diem × length of stay). Pay it
    /// once: the claim-level allowed amount (the amount priced on the first
    /// per-stay line) is allocated across the per-stay lines in proportion to
    /// each line's billed charge, each share truncated to the cent. When the
    /// group bills $0 in total, the amount is split evenly the same way.
    ///
    /// <para>
    /// The cent remainder from truncation goes to the last (highest numbered)
    /// line that still has room under its billed charge, working backwards, so
    /// while the claim-level allowed is at or below total billed every line's
    /// allowed stays at or below its billed charge and the per-line
    /// contractual adjustment (CO-45 = billed − allowed) is non-negative — a
    /// $0 or one-cent final line never absorbs the remainder. When the
    /// contract pays more than was billed (no lesser-of-billed provision) the
    /// remainder goes to the last line; every line's allowed then exceeds its
    /// billed charge, which the benefit engine pends rather than paying an
    /// unbalanced remittance. The shares always sum exactly to the claim-level
    /// amount.
    /// </para>
    ///
    /// <para>
    /// A claim has one stay, so it has one per-stay rate. When per-stay lines
    /// were priced from more than one schedule (contract procedure-code
    /// routing sent lines to different DRG / per-diem schedules), or at
    /// different claim-level amounts, there is no single authoritative rate:
    /// every per-stay line is marked <see cref="RateSource.Unresolved"/> so the
    /// claim pends for review instead of paying stacked stay rates. Per-line
    /// carve-outs priced from ordinary schedules are left as they are.
    /// </para>
    /// </summary>
    private static void AllocatePerStayAmounts(
        IReadOnlyList<LineResolution> initialResults, List<PricingResult> finalResults)
    {
        var indexes = Enumerable.Range(0, initialResults.Count)
            .Where(i => initialResults[i].IsPerStay)
            .OrderBy(i => finalResults[i].LineNumber)
            .ToList();
        if (indexes.Count == 0)
            return;

        var schedules = indexes
            .Select(i => (finalResults[i].FeeScheduleId, finalResults[i].FeeScheduleType))
            .Distinct()
            .ToList();
        var amounts = indexes.Select(i => finalResults[i].AllowedAmount).Distinct().ToList();
        if (schedules.Count > 1 || amounts.Count > 1)
        {
            var reason = schedules.Count > 1
                ? "conflicting per-stay rates: the claim's lines priced from more than one DRG / per-diem schedule (" +
                  string.Join(", ", schedules.Select(s => $"{s.FeeScheduleType} {s.FeeScheduleId}")) +
                  "); a stay is paid once, so no single rate can be selected"
                : "conflicting per-stay rates: the claim's per-stay lines priced at different claim-level amounts (" +
                  string.Join(", ", amounts.Select(a => a.ToString("0.00"))) + ")";
            foreach (var i in indexes)
            {
                finalResults[i] = finalResults[i] with
                {
                    AllowedAmount = 0m,
                    RateSource = RateSource.Unresolved,
                    UnresolvedReason = reason,
                    IsPerStayRate = false,
                };
            }
            return;
        }

        var claimRate = amounts[0];
        var rateName = schedules[0].FeeScheduleType == FeeScheduleType.Drg ? "DRG case rate" : "per diem";
        var billed = indexes.Select(i => BilledInCents(finalResults[i].BilledAmount)).ToList();
        var totalBilled = billed.Sum();

        // Lesser-of-billed for a stay compares the claim-level rate with the stay's
        // total billed charges, never a line's share with that line's charge.
        var claimAllowed = claimRate;
        if (indexes.All(i => initialResults[i].LesserOfBilled) && totalBilled < claimRate)
        {
            claimAllowed = totalBilled;
            foreach (var i in indexes)
            {
                finalResults[i] = finalResults[i] with
                {
                    AllowedAmount = claimAllowed,
                    LesserOfBilledApplied = true,
                    Adjustments = new List<RateAdjustment>(finalResults[i].Adjustments)
                    {
                        LesserOfAdjustment($"{rateName} for the stay", claimRate, totalBilled),
                    },
                };
            }
        }

        if (indexes.Count == 1)
            return; // single line: it already carries the whole amount
        var proportions = billed
            .Select(b => totalBilled > 0m ? b / totalBilled : 1m / indexes.Count)
            .ToList();
        var shares = proportions
            .Select(p => Math.Floor(claimAllowed * p * 100m) / 100m)
            .ToList();

        var remainder = claimAllowed - shares.Sum();
        if (claimAllowed <= totalBilled)
        {
            // Truncated shares never exceed billed here, and the total room
            // (totalBilled − Σ shares) covers the remainder.
            for (var n = indexes.Count - 1; n >= 0 && remainder > 0m; n--)
            {
                var room = billed[n] - shares[n];
                if (room <= 0m) continue;
                var add = Math.Min(room, remainder);
                shares[n] += add;
                remainder -= add;
            }
        }
        shares[^1] += remainder;

        for (var n = 0; n < indexes.Count; n++)
        {
            var i = indexes[n];
            var result = finalResults[i];
            var adjustments = new List<RateAdjustment>(result.Adjustments)
            {
                new()
                {
                    Modifier = string.Empty,
                    Description = $"{rateName} {claimAllowed:0.00} for the claim allocated by billed charges ({proportions[n]:P2} to line {result.LineNumber})",
                    AdjustmentFactor = Math.Round(proportions[n], 6),
                    AdjustmentAmount = shares[n] - result.AllowedAmount,
                },
            };

            finalResults[i] = result with
            {
                AllowedAmount = shares[n],
                Adjustments = adjustments,
            };
        }
    }

    /// <summary>
    /// Lesser-of-billed for one line: allowed = min(contract amount, billed charge).
    /// Lines priced at billed charges, unresolved lines and capitation are left alone.
    /// </summary>
    private static PricingResult ApplyLesserOfBilled(PricingResult result)
    {
        if (result.RateSource is RateSource.BilledCharges or RateSource.Unresolved or RateSource.Capitation)
            return result;

        var billed = BilledInCents(result.BilledAmount);
        if (result.AllowedAmount <= billed)
            return result;

        return result with
        {
            AllowedAmount = billed,
            LesserOfBilledApplied = true,
            Adjustments = new List<RateAdjustment>(result.Adjustments)
            {
                LesserOfAdjustment("contract rate", result.AllowedAmount, billed),
            },
        };
    }

    /// <summary>
    /// A billed charge as used for lesser-of and per-stay allocation: never negative,
    /// rounded to cents, so an allowed amount derived from it stays in whole cents.
    /// </summary>
    private static decimal BilledInCents(decimal billedAmount)
        => MoneyRounding.ToCents(Math.Max(billedAmount, 0m));

    private static RateAdjustment LesserOfAdjustment(string rateName, decimal rate, decimal billed) => new()
    {
        Modifier = string.Empty,
        Description = $"Lesser of {rateName} {rate:0.00} and billed charges {billed:0.00}: billed charges apply",
        AdjustmentFactor = rate > 0m ? Math.Round(billed / rate, 6) : 0m,
        AdjustmentAmount = billed - rate,
    };

    /// <summary>
    /// A line participates in multiple procedure ranking only when it was priced from a
    /// fee schedule line with MPFS indicator 2 (standard multiple surgery). Unresolved, billed-charge,
    /// DRG, per diem and capitation lines never participate, nor does a line whose
    /// contract term turns the reduction off
    /// (<see cref="TermModifierRules.MultipleProcedureReduction"/>).
    /// </summary>
    private static bool IsMultipleProcedureEligible(LineResolution line)
        => line.MultipleProcedureReduction && IsMultipleProcedureEligible(line.Result, line.RateLine);

    private static bool IsMultipleProcedureEligible(PricingResult result, FeeScheduleLine? rateLine)
        => rateLine is { MultipleProcedureReductionApplies: true }
           && result.RateSource is not (RateSource.Unresolved or RateSource.BilledCharges)
           && result.FeeScheduleType is not (FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation);

    /// <summary>
    /// Warnings for a line on a multi-line claim whose multiple procedure treatment is
    /// not handled by the 100/50/50 rule: no indicator in the source data (priced with
    /// no reduction — the safe default for E&amp;M, but missing data must be visible), or
    /// an indicator for a CMS rule the engine does not implement yet (3–7, or any other
    /// unrecognised value). Lines that never take part in multiple procedure pricing
    /// (unresolved, billed charges, DRG, per diem, capitation) are not flagged.
    /// </summary>
    private List<string> MultipleProcedureIndicatorWarnings(
        PricingRequest request, FeeScheduleLine? rateLine, RateSource rateSource, FeeScheduleType scheduleType)
    {
        if (rateLine is null
            || rateSource is RateSource.Unresolved or RateSource.BilledCharges
            || scheduleType is FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation)
            return [];

        string warning;
        switch (rateLine.MultipleProcedureIndicator)
        {
            case MultipleProcedureIndicator.NoReduction:
            case MultipleProcedureIndicator.StandardSurgery:
            case MultipleProcedureIndicator.NotApplicable:
                return [];

            case null:
                warning = $"No CMS multiple procedure indicator on the fee schedule line for {request.ProcedureCode}; " +
                          "multiple procedure reduction not applied";
                break;

            case var indicator:
                warning = $"Multiple procedure indicator {(byte)indicator} ({indicator}) for {request.ProcedureCode} " +
                          "is not yet supported; multiple procedure reduction not applied";
                break;
        }

        _logger.LogWarning(
            "Line {LineNumber} ({ProcedureCode}): {Warning}",
            request.LineNumber, LogSanitizer.Sanitize(request.ProcedureCode, 256), LogSanitizer.Sanitize(warning, 256));

        return [warning];
    }

    // ── Contract term resolution (ADR 016 §2) ──────────────────────────

    /// <summary>
    /// The contract rule that prices one claim line, read either from the first
    /// matching reimbursement term or from the contract's legacy fee schedule
    /// fields. The engine prices from this and never from the contract document
    /// again, so the two shapes meet in one place.
    /// </summary>
    /// <param name="FeeScheduleId">
    /// The schedule to price from, or null when the rule names none (no
    /// contract, no matching term, or a method that reads no schedule).
    /// </param>
    /// <param name="RateMultiplier">
    /// What the rule pays as a fraction of the rate it is based on: a fee
    /// schedule percentage, a percentage of the Medicare allowed amount, or a
    /// percentage of billed charges. 1.0 for every legacy contract.
    /// </param>
    /// <param name="Method">
    /// The term's method, or null when the line is priced the legacy way.
    /// Null is what tells the two paths apart.
    /// </param>
    /// <param name="MultipleProcedureReduction">False when the term turns the CMS multiple procedure reduction off.</param>
    /// <param name="BilateralAdjustment">False when the term turns the modifier-50 adjustment off.</param>
    /// <param name="LesserOfBilled">The term's lesser-of-billed override, or null to use the contract's setting.</param>
    /// <param name="PendReason">Why a matched term cannot price the line, or null when it can.</param>
    /// <param name="TermId">The matched term's id, for audit.</param>
    /// <param name="TermDescription">The matched term's description, for the audit trail.</param>
    private readonly record struct ContractTermResolution(
        string? FeeScheduleId,
        decimal RateMultiplier,
        ReimbursementMethod? Method,
        bool MultipleProcedureReduction,
        bool BilateralAdjustment,
        bool? LesserOfBilled,
        string? PendReason,
        string? TermId,
        string? TermDescription)
    {
        /// <summary>
        /// The rule in one phrase, for a pricing adjustment's description: the
        /// term's own description when it has one, else its method.
        /// </summary>
        public string Describe()
            => TermDescription is { Length: > 0 } description
                ? $"Contract term \"{description}\""
                : Method is { } method
                    ? $"Contract term ({method})"
                    : "Contract rate";
    }

    /// <summary>No contract, or a contract whose terms do not cover this line.</summary>
    private static readonly ContractTermResolution NoContractRule =
        new(null, 1.0m, null, true, true, null, null, null, null);

    /// <summary>
    /// The rule that prices <paramref name="request"/> under
    /// <paramref name="contract"/>.
    ///
    /// <para>
    /// A contract with no reimbursement terms — every contract written before
    /// ADR 016 phase 2 — is read exactly as before: its
    /// <see cref="ProviderContract.ContractLines"/> route procedure-code ranges
    /// to schedules and <see cref="ProviderContract.FeeScheduleId"/> is the
    /// last resort. A contract with terms is priced by the first term whose
    /// scope covers the line and whose dates cover the service date, and its
    /// legacy fields are not read, so the two shapes never combine.
    /// </para>
    ///
    /// <para>
    /// A contract with terms, none of which covers the line, is the same
    /// situation as a contract that names no schedule for it: the line falls
    /// through to the plan default. A term that <i>does</i> cover the line but
    /// cannot be priced carries a <see cref="ContractTermResolution.PendReason"/>
    /// instead, and the line is pended — the contract has an answer for this
    /// line and the engine cannot compute it, which is not the same as the
    /// contract being silent.
    /// </para>
    /// </summary>
    private static ContractTermResolution ResolveContractTerm(
        ProviderContract? contract, PricingRequest request)
    {
        if (contract is null)
            return NoContractRule;

        if (contract.ReimbursementTerms.Count == 0)
            return NoContractRule with
            {
                FeeScheduleId = ResolveScheduleId(contract, request.ProcedureCode),
            };

        var term = contract.ReimbursementTerms.FirstOrDefault(
            t => t.IsEffectiveOn(request.ServiceDate) && TermCovers(t.Scope, request));

        if (term is null)
            return NoContractRule;

        return new ContractTermResolution(
            FeeScheduleId: term.Method switch
            {
                ReimbursementMethod.PercentOfMedicare =>
                    string.IsNullOrWhiteSpace(term.BaseScheduleId) ? null : term.BaseScheduleId,
                ReimbursementMethod.PercentOfBilled or ReimbursementMethod.Capitation => (string?)null,
                _ => string.IsNullOrWhiteSpace(term.FeeScheduleId) ? null : term.FeeScheduleId,
            },
            RateMultiplier: term.Method switch
            {
                ReimbursementMethod.PercentOfMedicare or ReimbursementMethod.PercentOfBilled
                    => term.Percent ?? 1.0m,
                _ => term.Multiplier,
            },
            Method: term.Method,
            MultipleProcedureReduction: term.ModifierRules?.MultipleProcedureReduction ?? true,
            BilateralAdjustment: term.ModifierRules?.BilateralAdjustment ?? true,
            LesserOfBilled: term.LesserOfBilled,
            PendReason: TermPendReason(term),
            TermId: term.Id,
            TermDescription: term.Description);
    }

    /// <summary>
    /// Why a matched term cannot price the line, or null when it can. Fail
    /// closed: a term whose provisions the engine does not evaluate, or that
    /// is missing the field its method needs, pends the line rather than being
    /// paid as though the contract said something else. The term's own
    /// <c>Validate()</c> keeps such a document out of the store; this is the
    /// engine's guard for one that is already in it.
    /// </summary>
    private static string? TermPendReason(ReimbursementTerm term)
    {
        if (term.UnevaluatedProvisionReason is { } provision)
            return provision;

        return term.Method switch
        {
            ReimbursementMethod.FeeScheduleRef or ReimbursementMethod.CaseRate or ReimbursementMethod.PerDiem
                when string.IsNullOrWhiteSpace(term.FeeScheduleId)
                => $"contract term names no fee schedule for its {term.Method} method",

            ReimbursementMethod.PercentOfMedicare when string.IsNullOrWhiteSpace(term.BaseScheduleId)
                => "contract term names no Medicare base schedule for its percent-of-Medicare method",

            ReimbursementMethod.PercentOfMedicare or ReimbursementMethod.PercentOfBilled
                when term.Percent is null or <= 0m
                => $"contract term names no usable percentage for its {term.Method} method",

            ReimbursementMethod.FeeScheduleRef or ReimbursementMethod.CaseRate or ReimbursementMethod.PerDiem
                when term.Multiplier <= 0m
                => $"contract term's multiplier {term.Multiplier:0.####} is not a payable rate",

            ReimbursementMethod.FeeScheduleRef or ReimbursementMethod.PercentOfMedicare
                or ReimbursementMethod.PercentOfBilled or ReimbursementMethod.CaseRate
                or ReimbursementMethod.PerDiem or ReimbursementMethod.Capitation
                => null,

            // A document written by a later build, naming a method this one
            // does not implement. Never guess at it.
            var unknown => $"contract term names reimbursement method {(int)unknown}, " +
                           "which this build of the pricing engine does not implement",
        };
    }

    /// <summary>
    /// Whether a term's scope covers this line. Every condition the scope sets
    /// must hold; a condition it does not set matches everything, so an empty
    /// scope covers every line.
    /// </summary>
    private static bool TermCovers(ReimbursementTermScope scope, PricingRequest request)
    {
        if (scope.IsUnconditional)
            return true;

        if (scope.ProcedureCodeFrom is not null
            && !(request.ProcedureCode.Length > 0
                 && IsInCodeRange(request.ProcedureCode, scope.ProcedureCodeFrom, scope.ProcedureCodeTo)))
            return false;

        if (scope.RevenueCodes.Count > 0)
        {
            var revenueCode = NormalizeRevenueCode(request.RevenueCode);
            if (revenueCode is null
                || !scope.RevenueCodes.Any(c => string.Equals(
                        NormalizeRevenueCode(c), revenueCode, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        var institutional = InstitutionalClaimKind.IsInstitutional(request);

        // An institutional line's PlaceOfServiceCode holds the 837I facility
        // type code, not a CMS place of service, so it is never matched against
        // one: a term that names places of service does not cover it.
        if (scope.PlacesOfService.Count > 0)
        {
            if (institutional
                || !scope.PlacesOfService.Any(c => string.Equals(
                        c?.Trim(), request.PlaceOfServiceCode.Trim(), StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return scope.ClaimType switch
        {
            ContractClaimType.Any => true,
            ContractClaimType.Professional => !institutional,
            ContractClaimType.Institutional => institutional,
            ContractClaimType.InstitutionalInpatient =>
                institutional && InstitutionalClaimKind.IsInpatient(request) == true,
            ContractClaimType.InstitutionalOutpatient =>
                institutional && InstitutionalClaimKind.IsInpatient(request) == false,
            // A claim-type condition this build does not know never matches.
            _ => false,
        };
    }

    /// <summary>
    /// A line a matched contract term cannot price: allowed $0,
    /// <see cref="RateSource.Unresolved"/> and the reason, so the caller pends
    /// the claim. No schedule was read, so no schedule is reported.
    /// </summary>
    private LineResolution TermUnpriceable(
        PricingRequest request, NetworkStatus networkStatus, ContractTermResolution term)
    {
        _logger.LogWarning(
            "Contract term {TermId} cannot price {ProcedureCode}: {Reason}",
            LogSanitizer.Sanitize(term.TermId, 256), LogSanitizer.Sanitize(request.ProcedureCode, 256),
            LogSanitizer.Sanitize(term.PendReason, 512));

        return new LineResolution(new PricingResult
        {
            LineNumber       = request.LineNumber,
            ProcedureCode    = request.ProcedureCode,
            AllowedAmount    = 0m,
            BilledAmount     = request.BilledAmount,
            FeeScheduleType  = FeeScheduleType.Ucr,
            RateSource       = RateSource.Unresolved,
            NetworkStatus    = networkStatus,
            UnresolvedReason = term.PendReason,
        }, RateLine: null, IsPerStay: false, LesserOfBilled: false);
    }

    /// <summary>
    /// A line covered by a capitation term: the provider is paid PMPM for it, so
    /// the claim line is priced at $0 and reported as capitated. No fee schedule
    /// is read, and modifiers, units and lesser-of do not apply — the same
    /// treatment a capitation fee schedule gets.
    /// </summary>
    private static LineResolution CapitatedLine(
        PricingRequest request, NetworkStatus networkStatus, ContractTermResolution term)
        => new(new PricingResult
        {
            LineNumber      = request.LineNumber,
            ProcedureCode   = request.ProcedureCode,
            AllowedAmount   = 0m,
            BaseAmount      = 0m,
            BilledAmount    = request.BilledAmount,
            FeeScheduleType = FeeScheduleType.Capitation,
            RateSource      = RateSource.Capitation,
            NetworkStatus   = networkStatus,
            Adjustments     =
            [
                new RateAdjustment
                {
                    Modifier = string.Empty,
                    Description = $"{term.Describe()}: covered by the provider's capitation payment, not paid fee-for-service",
                    AdjustmentFactor = 0m,
                    AdjustmentAmount = -MoneyRounding.ToCents(Math.Max(request.BilledAmount, 0m)),
                },
            ],
        }, RateLine: null, IsPerStay: false, LesserOfBilled: false);

    /// <summary>
    /// A line covered by a percent-of-billed term: the allowed amount is a
    /// percentage of the provider's billed charge for the line. No fee schedule
    /// is read, so the line is never unpriceable for want of a rate. The amount
    /// already covers every unit on the line (<see cref="PricingRequest.BilledAmount"/>
    /// is the line total, 837 SV102/SV203), so units are not applied again —
    /// the same treatment a <see cref="FeeScheduleRateType.PercentOfBilled"/>
    /// schedule line gets. Modifier adjustments do apply, and a lesser-of-billed
    /// provision can only bind when the term pays more than 100% of billed.
    /// </summary>
    private LineResolution PercentOfBilledLine(
        PricingRequest request, NetworkStatus networkStatus, ContractTermResolution term,
        bool lesserOfBilled, bool applyMultipleProcedureReduction)
    {
        var baseAmount = MoneyRounding.ToCents(request.BilledAmount * term.RateMultiplier);

        var (finalAmount, modifierAdjustments) = ApplyModifierAdjustments(
            baseAmount, request, line: null,
            applyMultipleProcedureReduction && term.MultipleProcedureReduction,
            term.BilateralAdjustment);

        var adjustments = new List<RateAdjustment>
        {
            new()
            {
                Modifier = string.Empty,
                Description = $"{term.Describe()}: {term.RateMultiplier:P2} of billed charges {request.BilledAmount:0.00}",
                AdjustmentFactor = term.RateMultiplier,
                AdjustmentAmount = baseAmount - MoneyRounding.ToCents(request.BilledAmount),
            },
        };
        adjustments.AddRange(modifierAdjustments);

        return new LineResolution(new PricingResult
        {
            LineNumber      = request.LineNumber,
            ProcedureCode   = request.ProcedureCode,
            AllowedAmount   = MoneyRounding.ToCents(finalAmount),
            BaseAmount      = baseAmount,
            BilledAmount    = request.BilledAmount,
            FeeScheduleType = FeeScheduleType.Custom,
            RateSource      = RateSource.ContractedRate,
            NetworkStatus   = networkStatus,
            Adjustments     = adjustments,
        }, RateLine: null, IsPerStay: false, LesserOfBilled: lesserOfBilled,
            MultipleProcedureReduction: term.MultipleProcedureReduction);
    }

    /// <summary>
    /// A warning when a term's method does not match the schedule it names — a
    /// case-rate term pointing at an MPFS schedule, a fee-schedule term pointing
    /// at a DRG schedule. The schedule decides how the line prices either way
    /// (the engine prices by <see cref="FeeSchedule.Type"/>), so the amount is
    /// not in doubt; what the term says it pays is, and that is a contract
    /// loading error worth seeing. Null when they agree, or when there is no
    /// term or no schedule.
    /// </summary>
    private static string? TermMethodWarning(ContractTermResolution term, FeeSchedule? schedule)
    {
        if (term.Method is not { } method || schedule is null)
            return null;

        var expected = method switch
        {
            ReimbursementMethod.CaseRate => FeeScheduleType.Drg,
            ReimbursementMethod.PerDiem => FeeScheduleType.PerDiem,
            _ => (FeeScheduleType?)null,
        };

        if (expected is { } wanted && schedule.Type != wanted)
            return $"Contract term's {method} method names fee schedule {schedule.Id}, which is a " +
                   $"{schedule.Type} schedule, not {wanted}; the line priced as a {schedule.Type} rate";

        if (method is ReimbursementMethod.FeeScheduleRef
            && schedule.Type is FeeScheduleType.Drg or FeeScheduleType.PerDiem or FeeScheduleType.Capitation)
            return $"Contract term's fee-schedule reference names {schedule.Id}, a {schedule.Type} schedule; " +
                   $"the line priced as a {schedule.Type} rate, not a fee-schedule rate";

        return null;
    }

    // ── Schedule selection ─────────────────────────────────────────────

    /// <summary>
    /// The legacy contract shape: the first <see cref="ProviderContractLine"/>
    /// whose procedure-code range covers the code, else the contract's default
    /// <see cref="ProviderContract.FeeScheduleId"/>. Read only for a contract
    /// with no <see cref="ProviderContract.ReimbursementTerms"/>; ADR 016
    /// phase 3 backfills these two fields into terms.
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
        if (from is null) return true;

        var cmp = StringComparer.OrdinalIgnoreCase;

        if (to is null)
            return cmp.Compare(code, from) >= 0;

        return cmp.Compare(code, from) >= 0 && cmp.Compare(code, to) <= 0;
    }

    // ── Rate line lookup ───────────────────────────────────────────────

    /// <summary>
    /// Procedure code lookup — tries modifiers in claim order, then base rate.
    /// When no procedure-code line matches (or the claim line has no
    /// procedure code, as on a revenue-code-only 837I line) and a revenue
    /// code was billed, falls back to a revenue-code line: one whose
    /// <see cref="FeeScheduleLine.RevenueCode"/> matches and whose
    /// <see cref="FeeScheduleLine.ProcedureCode"/> is blank. Revenue-code
    /// lines honour <see cref="FeeScheduleLine.Modifier"/> the same way
    /// procedure lines do: a modifier-qualified line matches only when the
    /// claim line carries that modifier and wins over the unqualified rate.
    /// </summary>
    private static FeeScheduleLine? FindRateLine(
        FeeSchedule schedule, string procedureCode, IReadOnlyList<string> modifiers,
        string? revenueCode)
    {
        var byProcedure = string.IsNullOrEmpty(procedureCode)
            ? null
            : FindProcedureRateLine(schedule, procedureCode, modifiers);

        if (byProcedure is not null || string.IsNullOrEmpty(revenueCode))
            return byProcedure;

        var normalized = NormalizeRevenueCode(revenueCode);
        return SelectByModifier(
            schedule.Lines.Where(l =>
                string.IsNullOrEmpty(l.ProcedureCode)
                && string.Equals(NormalizeRevenueCode(l.RevenueCode), normalized, StringComparison.OrdinalIgnoreCase)),
            modifiers);
    }

    /// <summary>
    /// Revenue codes are four digits with a leading zero ("0120"), but are
    /// often keyed without it ("120"); compare on the four-digit form.
    /// </summary>
    private static string? NormalizeRevenueCode(string? revenueCode)
        => string.IsNullOrEmpty(revenueCode) ? null : revenueCode.Trim().PadLeft(4, '0');

    private static FeeScheduleLine? FindProcedureRateLine(
        FeeSchedule schedule, string procedureCode, IReadOnlyList<string> modifiers)
        => SelectByModifier(
            schedule.Lines.Where(l => string.Equals(l.ProcedureCode, procedureCode, StringComparison.OrdinalIgnoreCase)),
            modifiers);

    /// <summary>
    /// From the lines keyed to one code: the first modifier-qualified line
    /// whose modifier the claim line carries, else the unqualified (base)
    /// rate. A qualified line the claim does not carry never matches.
    /// </summary>
    private static FeeScheduleLine? SelectByModifier(
        IEnumerable<FeeScheduleLine> candidates, IReadOnlyList<string> modifiers)
    {
        FeeScheduleLine? baseRate = null;

        foreach (var line in candidates)
        {
            if (string.IsNullOrEmpty(line.Modifier))
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
    /// Async version of base amount calculation — needed for Medicaid and
    /// percent-of-Medicare cross-schedule resolution. A non-null
    /// <c>unresolvedReason</c> means a rate line matched but its base amount
    /// could not be determined; the amount is then meaningless. <c>isLineTotal</c>
    /// is true when the amount already covers every unit on the line (billed-charge
    /// based amounts, or per diem × length of stay) and must not be multiplied by
    /// units again.
    /// </summary>
    private async Task<(decimal amount, RateSource source, FeeScheduleType scheduleType, string? unresolvedReason, bool isLineTotal)> CalculateBaseAmountAsync(
        PricingRequest request,
        FeeSchedule? schedule,
        FeeScheduleLine? line,
        NetworkStatus networkStatus,
        CancellationToken ct)
    {
        if (schedule is null || line is null)
        {
            return (request.BilledAmount, RateSource.BilledCharges, FeeScheduleType.Ucr, null, true);
        }

        switch (schedule.Type)
        {
            case FeeScheduleType.Capitation:
                return (0m, RateSource.Capitation, FeeScheduleType.Capitation, null, false);

            case FeeScheduleType.PerDiem:
            {
                var rate = schedule.PerDiemRate ?? line.Rate;

                // All-inclusive per diem (schedule-level PerDiemRate) with a length of
                // stay: rate × LOS covers the whole stay — paid once per claim by
                // ResolveBatchAsync — and units (= days on accommodation lines) are not
                // applied again. Otherwise units are the day count (step 6): a line-level
                // daily rate (e.g. room and board keyed by revenue code) prices each
                // accommodation line by the days it bills, so a claim-level LOS sent on
                // every line never multiplies each line by the whole stay.
                if (schedule.PerDiemRate.HasValue && request.LengthOfStay is { } los)
                    return (rate * los, RateSource.PerDiem, FeeScheduleType.PerDiem, null, true);

                return (rate, RateSource.PerDiem, FeeScheduleType.PerDiem, null, false);
            }

            case FeeScheduleType.Drg:
            {
                var drgRate = line.Rate;
                // If DRG weight is specified, rate = base rate × weight
                if (line.DrgWeight.HasValue && line.DrgWeight.Value > 0)
                    drgRate = (schedule.DrgBaseRate ?? line.Rate) * line.DrgWeight.Value;
                return (MoneyRounding.ToCents(drgRate), RateSource.Drg, FeeScheduleType.Drg, null, false);
            }

            case FeeScheduleType.MedicareMpfs:
            case FeeScheduleType.MedicareOpps:
            {
                var amount = CalculateMedicareLineAmount(schedule, line, IsFacilitySetting(request));
                return (amount, RateSource.MedicareMpfs, schedule.Type, null, false);
            }

            case FeeScheduleType.Medicaid:
            {
                var (amount, failure) = await ResolveMedicaidRateAsync(
                    request, schedule, line, ct);
                return (amount, RateSource.Medicaid, FeeScheduleType.Medicaid, failure, false);
            }

            default: // Commercial, Custom
            {
                var source = schedule.Type == FeeScheduleType.Commercial
                    ? RateSource.ContractedRate
                    : RateSource.PlanDefault;

                if (line.RateType == FeeScheduleRateType.PercentOfMedicare)
                {
                    // line.Rate is a multiplier on the Medicare allowed amount (e.g. 1.10 = 110%),
                    // never on billed charges.
                    var (medicareRate, failure) = await ResolveMedicareBaseRateAsync(
                        request, schedule, line, ct);

                    if (medicareRate is null)
                        return (0m, source, schedule.Type, failure, false);

                    return (MoneyRounding.ToCents(medicareRate.Value * line.Rate), source, schedule.Type, null, false);
                }

                if (line.RateType == FeeScheduleRateType.PercentOfBilled)
                    return (request.BilledAmount * line.Rate, source, schedule.Type, null, true);

                return (FlatLineRate(line, IsFacilitySetting(request)), source, schedule.Type, null, false);
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
    private async Task<(decimal amount, string? failureReason)> ResolveMedicaidRateAsync(
        PricingRequest request,
        FeeSchedule medicaidSchedule,
        FeeScheduleLine medicaidLine,
        CancellationToken ct)
    {
        // Strategy 1: Flat rate (no RVU, no percent-of-Medicare, or rate already pre-calculated)
        if (medicaidLine.RateType == FeeScheduleRateType.FlatRate
            && !medicaidSchedule.PercentOfMedicare.HasValue)
        {
            return (FlatLineRate(medicaidLine, IsFacilitySetting(request)), null);
        }

        // Strategy 3: Inline RVU on the Medicaid line itself
        if (medicaidLine.RateType == FeeScheduleRateType.Rvu)
        {
            var rvuAmount = CalculateRvuAmount(medicaidSchedule, medicaidLine, IsFacilitySetting(request));
            if (medicaidSchedule.PercentOfMedicare.HasValue)
                rvuAmount *= medicaidSchedule.PercentOfMedicare.Value;
            return (MoneyRounding.ToCents(rvuAmount), null);
        }

        // Strategy 2: Cross-schedule lookup — load the base Medicare MPFS schedule
        if (medicaidSchedule.BaseMpfsFeeScheduleId is not null
            && medicaidSchedule.PercentOfMedicare.HasValue)
        {
            var baseSchedule = await _feeScheduleRepo.GetByIdAsync(
                request.TenantId, medicaidSchedule.BaseMpfsFeeScheduleId, ct);

            if (baseSchedule is not null)
            {
                var baseLine = FindRateLine(baseSchedule, request.ProcedureCode, request.Modifiers, request.RevenueCode);
                if (baseLine is { RateType: FeeScheduleRateType.Rvu }
                    && !baseSchedule.ConversionFactor.HasValue)
                {
                    // CalculateRvuAmount would fall back to the stored Rate, which is not
                    // maintained for RVU lines — not a usable Medicare rate.
                    return (0m,
                        $"Medicare reference schedule {medicaidSchedule.BaseMpfsFeeScheduleId} has no " +
                        $"conversion factor to price RVU line {request.ProcedureCode}");
                }

                if (baseLine is not null)
                {
                    var medicareRate = CalculateMedicareLineAmount(
                        baseSchedule, baseLine, IsFacilitySetting(request));

                    var medicaidRate = medicareRate * medicaidSchedule.PercentOfMedicare.Value;

                    _logger.LogDebug(
                        "Medicaid cross-schedule: {ProcedureCode} Medicare={MedicareRate:C} " +
                        "× {Percent:P0} = {MedicaidRate:C}",
                        LogSanitizer.Sanitize(request.ProcedureCode, 256), medicareRate,
                        medicaidSchedule.PercentOfMedicare.Value, medicaidRate);

                    return (MoneyRounding.ToCents(medicaidRate), null);
                }

                _logger.LogWarning(
                    "Medicaid cross-schedule: base MPFS schedule {ScheduleId} has no line " +
                    "for {ProcedureCode}; falling back to Medicaid line rate",
                    LogSanitizer.Sanitize(medicaidSchedule.BaseMpfsFeeScheduleId, 256),
                    LogSanitizer.Sanitize(request.ProcedureCode, 256));
            }
            else
            {
                _logger.LogWarning(
                    "Medicaid cross-schedule: base MPFS schedule {ScheduleId} not found; " +
                    "falling back to Medicaid line rate",
                    LogSanitizer.Sanitize(medicaidSchedule.BaseMpfsFeeScheduleId, 256));
            }
        }

        // Fallback: use the Medicaid line's stored rate, apply percent if configured
        var fallbackRate = medicaidLine.Rate;
        if (medicaidSchedule.PercentOfMedicare.HasValue)
            fallbackRate *= medicaidSchedule.PercentOfMedicare.Value;

        return (MoneyRounding.ToCents(fallbackRate), null);
    }

    // ── Percent-of-Medicare base resolution (Commercial / Custom) ──────

    /// <summary>
    /// Resolves the Medicare allowed amount that a PercentOfMedicare line's
    /// multiplier applies to. Sources, in order:
    ///
    /// 1. The Medicare reference schedule named by
    ///    <see cref="FeeSchedule.BaseMpfsFeeScheduleId"/> (flat or RVU line,
    ///    same lookup as Medicaid cross-schedule pricing).
    /// 2. RVUs stored inline on the line itself, priced with the schedule's
    ///    GPCI × ConversionFactor.
    ///
    /// Falls through to (2) when the reference schedule is missing or unusable;
    /// returns a null rate with a reason only when neither yields a Medicare amount.
    /// </summary>
    private async Task<(decimal? medicareRate, string? failureReason)> ResolveMedicareBaseRateAsync(
        PricingRequest request,
        FeeSchedule schedule,
        FeeScheduleLine line,
        CancellationToken ct)
    {
        string? referenceFailure = null;

        if (schedule.BaseMpfsFeeScheduleId is not null)
        {
            var baseSchedule = await _feeScheduleRepo.GetByIdAsync(
                request.TenantId, schedule.BaseMpfsFeeScheduleId, ct);

            if (baseSchedule is null)
            {
                referenceFailure = $"Medicare reference schedule {schedule.BaseMpfsFeeScheduleId} not found";
            }
            else
            {
                var baseLine = FindRateLine(baseSchedule, request.ProcedureCode, request.Modifiers, request.RevenueCode);
                var usable = baseLine?.RateType switch
                {
                    FeeScheduleRateType.FlatRate => true,
                    // An RVU line needs a conversion factor; CalculateRvuAmount would otherwise
                    // fall back to the stored Rate, which is not maintained for RVU lines.
                    FeeScheduleRateType.Rvu      => baseSchedule.ConversionFactor.HasValue,
                    _                            => false,
                };

                if (usable)
                    return (CalculateMedicareLineAmount(baseSchedule, baseLine!, IsFacilitySetting(request)), null);

                referenceFailure =
                    $"Medicare reference schedule {schedule.BaseMpfsFeeScheduleId} has no usable Medicare rate " +
                    $"for {request.ProcedureCode}";
            }
        }

        // Inline RVUs: only usable when a conversion factor is configured — CalculateRvuAmount
        // otherwise falls back to line.Rate, which here is the percentage multiplier.
        if (schedule.ConversionFactor.HasValue
            && (line.WorkRvu.HasValue || line.PeRvu.HasValue || line.PeRvuFacility.HasValue || line.MpRvu.HasValue))
        {
            if (referenceFailure is not null)
            {
                _logger.LogWarning(
                    "Percent-of-Medicare for {ProcedureCode}: {Reason}; using inline RVUs",
                    LogSanitizer.Sanitize(request.ProcedureCode, 256), LogSanitizer.Sanitize(referenceFailure, 256));
            }

            return (CalculateRvuAmount(schedule, line, IsFacilitySetting(request)), null);
        }

        return (null, referenceFailure
            ?? $"Percent-of-Medicare rate for {request.ProcedureCode} has no Medicare reference schedule " +
               "or inline RVUs to price against");
    }

    // ── RVU calculation ───────────────────────────────────────────────

    /// <summary>Medicare allowed amount for a Medicare schedule line (RVU-based or stored flat rate).</summary>
    private static decimal CalculateMedicareLineAmount(
        FeeSchedule schedule, FeeScheduleLine line, bool isFacility)
        => line.RateType == FeeScheduleRateType.Rvu
            ? CalculateRvuAmount(schedule, line, isFacility)
            : FlatLineRate(line, isFacility);

    /// <summary>
    /// The dollar rate of a flat-rate line: its <see cref="FeeScheduleLine.FacilityRate"/>
    /// in a facility place of service when one is set, otherwise <see cref="FeeScheduleLine.Rate"/>.
    /// </summary>
    private static decimal FlatLineRate(FeeScheduleLine line, bool isFacility)
        => line.RateType == FeeScheduleRateType.FlatRate
           && line.FacilityRate is { } facilityRate
           && isFacility
            ? facilityRate
            : line.Rate;

    private static decimal CalculateRvuAmount(
        FeeSchedule schedule, FeeScheduleLine line, bool isFacility)
    {
        if (!schedule.ConversionFactor.HasValue)
            return line.Rate; // fall back to stored rate if CF missing

        var peRvu = (isFacility ? line.PeRvuFacility : line.PeRvu) ?? line.PeRvu ?? 0m;

        var total = (line.WorkRvu ?? 0m) * schedule.WorkGpci
                  + peRvu                 * schedule.PeGpci
                  + (line.MpRvu ?? 0m)   * schedule.MpGpci;

        return MoneyRounding.ToCents(total * schedule.ConversionFactor.Value);
    }

    // ── Modifier adjustments ───────────────────────────────────────────

    /// <summary>
    /// <paramref name="bilateralAdjustmentApplies"/> is the contract term's
    /// bilateral toggle (<see cref="TermModifierRules.BilateralAdjustment"/>):
    /// false suppresses the modifier-50 adjustment whatever the rate line says.
    /// True — every legacy contract, and a term that does not override it — leaves
    /// the decision to the rate line, as before.
    /// </summary>
    private (decimal finalAmount, IReadOnlyList<RateAdjustment> adjustments) ApplyModifierAdjustments(
        decimal baseAmount,
        PricingRequest request,
        FeeScheduleLine? line,
        bool applyMultipleProcedureReduction,
        bool bilateralAdjustmentApplies = true)
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
            && bilateralAdjustmentApplies
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

        // 80 / 81 / 82 — assistant surgeon (16%). CMS pays all three assistant
        // surgeon modifiers at 16% of the primary rate; applied once per line.
        var assistantModifier = AssistantSurgeonModifiers.FirstOrDefault(
            m => modifiers.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (assistantModifier is not null)
        {
            if (line?.AssistantAtSurgeryAllowed ?? true)
            {
                var reduced = amount * 0.16m;
                var adj = reduced - amount;
                adjustments.Add(Adjustment(assistantModifier,
                    "Assistant surgeon (16% of primary rate)", 0.16m, adj));
                amount = reduced;
            }
            else
            {
                var adj = -amount;
                adjustments.Add(Adjustment(assistantModifier,
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
        // ResolveBatchAsync is not used (single-line ResolveAsync calls); batch pricing
        // suppresses it so a modifier-51 line is not reduced twice.
        // Only standard multiple surgery lines (MPFS indicator 2) are reduced; modifier 51
        // on a line whose indicator is 0/9/unknown/unsupported does not trigger the 50% rule.
        if (applyMultipleProcedureReduction
            && line is { MultipleProcedureReductionApplies: true }
            && (modifiers.Contains(PaymentModifiers.MultipleProcedures, StringComparer.OrdinalIgnoreCase)
                || (request.LineNumber > 1 && request.TotalLineCount > 1)))
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
