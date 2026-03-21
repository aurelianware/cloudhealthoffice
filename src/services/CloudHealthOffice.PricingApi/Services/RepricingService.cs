using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;

namespace CloudHealthOffice.PricingApi.Services;

public interface IRepricingService
{
    Task<RepricingResponse> RepriceClaimAsync(RepricingRequest request);
    Task<CodeLookupResponse?> LookupCodeAsync(CodeLookupRequest request);
}

public class RepricingService : IRepricingService
{
    private readonly IFeeScheduleRepository _feeScheduleRepo;
    private readonly ILogger<RepricingService> _logger;

    public RepricingService(IFeeScheduleRepository feeScheduleRepo, ILogger<RepricingService> logger)
    {
        _feeScheduleRepo = feeScheduleRepo;
        _logger = logger;
    }

    public async Task<RepricingResponse> RepriceClaimAsync(RepricingRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var warnings = new List<string>();

        // Validate fee schedule exists
        var scheduleInfo = await _feeScheduleRepo.GetScheduleInfoAsync(request.FeeScheduleId);
        if (scheduleInfo is null)
            throw new InvalidOperationException($"Fee schedule '{request.FeeScheduleId}' not found.");

        var pricedLines = new List<PricedLine>();

        if (request.ClaimType == ClaimType.Inpatient)
        {
            // DRG-based pricing — price at the claim level, not per-line
            pricedLines = await PriceInpatientClaimAsync(request, warnings);
        }
        else
        {
            // Line-level pricing (Professional / Outpatient)
            var procedureCodes = request.Lines.Select(l => l.ProcedureCode).Distinct();
            var entries = await _feeScheduleRepo.LookupCodesAsync(
                request.FeeScheduleId, procedureCodes, request.Locality);

            var entryMap = entries.ToDictionary(
                e => e.ProcedureCode,
                e => e,
                StringComparer.OrdinalIgnoreCase);

            // Sort lines for multiple procedure ranking
            var sortedLines = request.Lines
                .OrderByDescending(l => GetBaseRate(entryMap, l, request))
                .ToList();

            for (var rank = 0; rank < sortedLines.Count; rank++)
            {
                var line = sortedLines[rank];
                var pricedLine = await PriceLineAsync(line, entryMap, request, rank, warnings);
                pricedLines.Add(pricedLine);
            }

            // Re-sort by original line number
            pricedLines = pricedLines.OrderBy(l => l.LineNumber).ToList();
        }

        return new RepricingResponse
        {
            RequestId = requestId,
            FeeScheduleId = request.FeeScheduleId,
            FeeScheduleVersion = scheduleInfo.Version,
            ClaimType = request.ClaimType,
            DrgCode = request.DrgCode,
            TotalAllowed = pricedLines.Sum(l => l.AllowedAmount),
            TotalBilled = request.Lines.Any(l => l.BilledAmount.HasValue)
                ? request.Lines.Sum(l => l.BilledAmount ?? 0)
                : null,
            Lines = pricedLines,
            Warnings = warnings.Count > 0 ? warnings : null,
            PricedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<CodeLookupResponse?> LookupCodeAsync(CodeLookupRequest request)
    {
        var entry = await _feeScheduleRepo.LookupCodeAsync(
            request.FeeScheduleId, request.ProcedureCode, request.Locality);

        if (entry is null)
            return null;

        var rate = request.Facility
            ? (entry.FacilityRate ?? entry.ApcPaymentRate ?? 0)
            : (entry.NonFacilityRate ?? entry.ApcPaymentRate ?? 0);

        return new CodeLookupResponse
        {
            ProcedureCode = entry.ProcedureCode,
            Description = entry.Description,
            FeeScheduleId = entry.FeeScheduleId,
            Locality = entry.Locality,
            AllowedAmount = rate,
            WorkRvu = entry.WorkRvu,
            PracticeExpenseRvu = request.Facility ? entry.PracticeExpenseRvuFacility : entry.PracticeExpenseRvu,
            MalpracticeRvu = entry.MalpracticeRvu,
            TotalRvu = request.Facility ? entry.TotalRvuFacility : entry.TotalRvuNonFacility,
            ConversionFactor = entry.ConversionFactor,
            StatusIndicator = entry.StatusIndicator,
            ApcCode = entry.ApcCode,
            Facility = request.Facility
        };
    }

    // ─────────────────────────────────────────────────────────
    //  Private pricing methods
    // ─────────────────────────────────────────────────────────

    private async Task<List<PricedLine>> PriceInpatientClaimAsync(
        RepricingRequest request, List<string> warnings)
    {
        var drgCode = request.DrgCode;
        if (string.IsNullOrEmpty(drgCode))
        {
            warnings.Add("No DRG code provided. Inpatient pricing requires a valid MS-DRG. Provide DrgCode or ensure diagnoses support DRG grouping.");
            return request.Lines.Select(l => new PricedLine
            {
                LineNumber = l.LineNumber,
                ProcedureCode = l.ProcedureCode,
                Modifiers = l.Modifiers,
                Units = l.Units,
                AllowedAmount = 0,
                BilledAmount = l.BilledAmount,
                Breakdown = new PricingBreakdown(),
                Status = PricingStatus.NotFound,
                StatusReason = "DRG code required for inpatient pricing"
            }).ToList();
        }

        var drgEntry = await _feeScheduleRepo.LookupDrgAsync(request.FeeScheduleId, drgCode);
        if (drgEntry is null)
        {
            warnings.Add($"DRG {drgCode} not found in fee schedule {request.FeeScheduleId}.");
            return request.Lines.Select(l => new PricedLine
            {
                LineNumber = l.LineNumber,
                ProcedureCode = l.ProcedureCode,
                Modifiers = l.Modifiers,
                Units = l.Units,
                AllowedAmount = 0,
                BilledAmount = l.BilledAmount,
                Breakdown = new PricingBreakdown(),
                Status = PricingStatus.NotFound,
                StatusReason = $"DRG {drgCode} not found"
            }).ToList();
        }

        var drgPayment = (drgEntry.DrgWeight ?? 0) * (drgEntry.DrgBaseRate ?? 0);

        // For DRG, the payment is at the claim level — assign to line 1
        return request.Lines.Select((l, idx) => new PricedLine
        {
            LineNumber = l.LineNumber,
            ProcedureCode = l.ProcedureCode,
            Modifiers = l.Modifiers,
            Units = l.Units,
            AllowedAmount = idx == 0 ? drgPayment : 0, // Full DRG payment on first line
            BilledAmount = l.BilledAmount,
            Breakdown = new PricingBreakdown
            {
                BaseRate = drgEntry.DrgBaseRate ?? 0,
                DrgRelativeWeight = drgEntry.DrgWeight,
                HospitalBaseRate = drgEntry.DrgBaseRate
            },
            Status = PricingStatus.Priced,
            StatusReason = idx == 0 ? null : "Bundled under DRG payment"
        }).ToList();
    }

    private Task<PricedLine> PriceLineAsync(
        ClaimLineRequest line,
        Dictionary<string, FeeScheduleEntry> entryMap,
        RepricingRequest request,
        int multiProcRank,
        List<string> warnings)
    {
        if (!entryMap.TryGetValue(line.ProcedureCode, out var entry))
        {
            warnings.Add($"Line {line.LineNumber}: Code {line.ProcedureCode} not found in {request.FeeScheduleId}.");
            return Task.FromResult(new PricedLine
            {
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
                Modifiers = line.Modifiers,
                Units = line.Units,
                AllowedAmount = 0,
                BilledAmount = line.BilledAmount,
                Breakdown = new PricingBreakdown(),
                Status = PricingStatus.NotFound,
                StatusReason = $"Code {line.ProcedureCode} not found in fee schedule"
            });
        }

        // Determine facility vs non-facility
        var isFacility = IsFacilityPos(request.PlaceOfService);
        var baseRate = isFacility
            ? (entry.FacilityRate ?? entry.ApcPaymentRate ?? 0)
            : (entry.NonFacilityRate ?? entry.ApcPaymentRate ?? 0);

        // Apply modifier adjustments
        var modifierFactor = CalculateModifierFactor(line.Modifiers, warnings, line.LineNumber);

        // Apply multiple procedure reduction (standard CMS rules)
        var multiProcFactor = CalculateMultiProcFactor(multiProcRank, line.Modifiers);
        if (multiProcFactor < 1.0m)
        {
            warnings.Add($"Line {line.LineNumber}: Multiple procedure reduction applied ({multiProcFactor:P0}).");
        }

        var allowedAmount = Math.Round(baseRate * line.Units * modifierFactor * multiProcFactor, 2);

        return Task.FromResult(new PricedLine
        {
            LineNumber = line.LineNumber,
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers,
            Units = line.Units,
            AllowedAmount = allowedAmount,
            BilledAmount = line.BilledAmount,
            Breakdown = new PricingBreakdown
            {
                BaseRate = baseRate,
                FacilityIndicator = isFacility ? "Facility" : "Non-Facility",
                WorkRvu = entry.WorkRvu,
                PracticeExpenseRvu = isFacility ? entry.PracticeExpenseRvuFacility : entry.PracticeExpenseRvu,
                MalpracticeRvu = entry.MalpracticeRvu,
                ConversionFactor = entry.ConversionFactor,
                MultiProcReduction = multiProcFactor < 1.0m ? multiProcFactor : null,
                ModifierAdjustment = modifierFactor != 1.0m ? $"Factor: {modifierFactor}" : null,
                ApcCode = entry.ApcCode
            },
            Status = PricingStatus.Priced
        });
    }

    /// <summary>
    /// Standard CMS modifier payment adjustments.
    /// </summary>
    private static decimal CalculateModifierFactor(List<string>? modifiers, List<string> warnings, int lineNumber)
    {
        if (modifiers is null or { Count: 0 })
            return 1.0m;

        var factor = 1.0m;

        foreach (var mod in modifiers.Select(m => m.ToUpperInvariant()))
        {
            factor *= mod switch
            {
                "50" => 1.5m,      // Bilateral — 150%
                "52" => 0.5m,      // Reduced services — 50% (plan-specific, default)
                "26" => 1.0m,      // Professional component — handled by PC/TC split in fee schedule
                "TC" => 1.0m,      // Technical component — same
                "80" => 0.16m,     // Assistant surgeon — 16%
                "81" => 0.10m,     // Minimum assistant surgeon — 10%
                "82" => 0.16m,     // Assistant surgeon (no qualified resident)
                "62" => 0.625m,    // Co-surgeon — 62.5% each
                "66" => 0.25m,     // Team surgery — varies, default 25%
                "51" => 1.0m,      // Multiple procedures — handled by multi-proc logic
                "59" => 1.0m,      // Distinct procedural service — bypasses bundling
                "25" => 1.0m,      // Significant, separately identifiable E/M
                "76" => 1.0m,      // Repeat procedure by same physician
                "77" => 1.0m,      // Repeat procedure by different physician
                "78" => 1.0m,      // Unplanned return to OR — related procedure
                "79" => 1.0m,      // Unrelated procedure during postop
                _ => 1.0m
            };
        }

        return factor;
    }

    /// <summary>
    /// Standard CMS Multiple Procedure Payment Reduction (MPPR).
    /// Highest-valued procedure at 100%, subsequent at 50% for the PE component.
    /// Simplified: rank 0 = 100%, rank 1+ = 50%.
    /// </summary>
    private static decimal CalculateMultiProcFactor(int rank, List<string>? modifiers)
    {
        // Modifier 59 or XE/XS/XP/XU bypass bundling but not MPPR
        // Modifier 51 explicitly flags multiple procedures
        if (rank == 0) return 1.0m;

        // Check if modifier 51 is present or if there are multiple surgical procedures
        return 0.5m;
    }

    private static bool IsFacilityPos(string? placeOfService)
    {
        // CMS facility POS codes
        return placeOfService switch
        {
            "21" => true,  // Inpatient Hospital
            "22" => true,  // On-Campus Outpatient Hospital
            "23" => true,  // Emergency Room
            "24" => true,  // Ambulatory Surgical Center
            "26" => true,  // Military Treatment Facility
            "31" => true,  // Skilled Nursing Facility
            "34" => true,  // Hospice
            "41" => true,  // Ambulance (Land)
            "42" => true,  // Ambulance (Air/Water)
            "51" => true,  // Inpatient Psychiatric
            "52" => true,  // Psychiatric Facility (Partial Hosp)
            "53" => true,  // Community Mental Health Center
            "56" => true,  // Psychiatric Residential Treatment
            "61" => true,  // Comprehensive Inpatient Rehab
            "71" => true,  // State/Local Public Health Clinic
            _ => false      // Office (11), Home (12), etc. = Non-Facility
        };
    }

    private static decimal GetBaseRate(
        Dictionary<string, FeeScheduleEntry> entryMap,
        ClaimLineRequest line,
        RepricingRequest request)
    {
        if (!entryMap.TryGetValue(line.ProcedureCode, out var entry))
            return 0;

        return IsFacilityPos(request.PlaceOfService)
            ? (entry.FacilityRate ?? 0)
            : (entry.NonFacilityRate ?? 0);
    }
}
