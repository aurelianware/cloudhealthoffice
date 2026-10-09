using System.Globalization;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace CloudHealthOffice.PricingApi.Services;

public interface IRepricingService
{
    Task<RepricingResponse> RepriceClaimAsync(RepricingRequest request);
    Task<CodeLookupResponse?> LookupCodeAsync(CodeLookupRequest request);
}

/// <summary>
/// Reprices a claim against a named fee schedule.
///
/// <para>
/// Pricing is delegated to <see cref="RateResolutionService"/> — the engine claims
/// adjudication prices with (benefit-plan-service <c>resolve-rates</c>,
/// claims-service <c>PricingStage</c>) — so the Pricing API and adjudication return
/// the same allowed amount for the same claim and contract (ADR 016). This class only
/// translates: the request into the engine's <see cref="EngineModels.PricingRequest"/>
/// lines, the schedule into the engine's model (<see cref="IPricingScheduleSource"/>),
/// and the engine's results back into the public response, which is unchanged.
/// </para>
///
/// <para>
/// One presentation rule differs from adjudication: a line the schedule has no rate
/// for is reported <see cref="PricingStatus.NotFound"/> at $0, where adjudication's
/// engine result falls back to billed charges (and then pends the claim).
/// </para>
/// </summary>
public class RepricingService : IRepricingService
{
    private readonly IFeeScheduleRepository _feeScheduleRepo;
    private readonly IPricingScheduleSource _scheduleSource;
    private readonly ILogger<RepricingService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public RepricingService(IFeeScheduleRepository feeScheduleRepo, ILogger<RepricingService> logger)
        : this(feeScheduleRepo, new LegacyEntryScheduleSource(feeScheduleRepo), logger, NullLoggerFactory.Instance)
    {
    }

    public RepricingService(
        IFeeScheduleRepository feeScheduleRepo,
        IPricingScheduleSource scheduleSource,
        ILogger<RepricingService> logger,
        ILoggerFactory loggerFactory)
    {
        _feeScheduleRepo = feeScheduleRepo;
        _scheduleSource = scheduleSource;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<RepricingResponse> RepriceClaimAsync(RepricingRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var warnings = new List<string>();

        var query = new PricingScheduleQuery(
            request.ClaimType,
            request.Locality,
            request.Lines.Select(l => l.ProcedureCode).ToList(),
            request.DrgCode);

        var loaded = await _scheduleSource.LoadAsync(request.FeeScheduleId, query)
            ?? throw new InvalidOperationException($"Fee schedule '{request.FeeScheduleId}' not found.");

        List<PricedLine> pricedLines;
        if (loaded.Schedule.Type == EngineDomain.FeeScheduleType.Drg && string.IsNullOrEmpty(request.DrgCode))
        {
            // Cloud Health Office has no MS-DRG grouper: an inpatient claim without a DRG cannot be priced.
            warnings.Add("No DRG code provided. Inpatient pricing requires a valid MS-DRG. Provide DrgCode or ensure diagnoses support DRG grouping.");
            pricedLines = request.Lines.Select(l => NotPriced(l, "DRG code required for inpatient pricing")).ToList();
        }
        else
        {
            pricedLines = await PriceWithEngineAsync(request, loaded, warnings);
        }

        return new RepricingResponse
        {
            RequestId = requestId,
            FeeScheduleId = request.FeeScheduleId,
            FeeScheduleVersion = loaded.Version,
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
            MultipleProcedureIndicator = entry.MultipleProcedureIndicator,
            Facility = request.Facility
        };
    }

    // ─────────────────────────────────────────────────────────
    //  Engine translation
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// The engine request for each claim line, in claim order. Lines are numbered
    /// by position (the public request may repeat or omit line numbers); the
    /// engine's multiple procedure tie-break and per-stay allocation order then
    /// follow the order the caller sent.
    /// </summary>
    internal static List<EngineModels.PricingRequest> BuildEngineRequests(RepricingRequest request)
    {
        var placeOfService = string.IsNullOrWhiteSpace(request.PlaceOfService)
            ? "11"
            : request.PlaceOfService.Trim();
        var today = DateTime.UtcNow.Date;

        return request.Lines.Select((line, index) => new EngineModels.PricingRequest
        {
            TenantId = RepricingScheduleStore.TenantId,
            ProcedureCode = line.ProcedureCode ?? string.Empty,
            Modifiers = line.Modifiers ?? [],
            ProviderNpi = RepricingScheduleStore.ProviderNpi,
            PlanId = RepricingScheduleStore.PlanId,
            PlaceOfServiceCode = placeOfService,
            ServiceDate = line.ServiceDate?.ToDateTime(TimeOnly.MinValue) ?? today,
            BilledAmount = line.BilledAmount ?? 0m,
            Units = line.Units,
            LineNumber = index + 1,
            TotalLineCount = request.Lines.Count,
            DrgCode = string.IsNullOrWhiteSpace(request.DrgCode) ? null : request.DrgCode.Trim(),
            RevenueCode = line.RevenueCode,
        }).ToList();
    }

    private async Task<List<PricedLine>> PriceWithEngineAsync(
        RepricingRequest request, LoadedPricingSchedule loaded, List<string> warnings)
    {
        var store = new RepricingScheduleStore(_scheduleSource, loaded.Schedule);
        var engine = new RateResolutionService(store, store, _loggerFactory.CreateLogger<RateResolutionService>());

        var engineRequests = BuildEngineRequests(request);
        var resultSet = await engine.ResolveBatchAsync(engineRequests);
        var results = resultSet.LineResults.ToDictionary(r => r.LineNumber);

        var isFacility = RateResolutionService.IsFacilityPlaceOfService(engineRequests.FirstOrDefault()?.PlaceOfServiceCode);
        var perStayLines = resultSet.LineResults.Count(r => r.IsPerStayRate);
        var drgNotFoundReported = false;
        var priced = new List<PricedLine>(request.Lines.Count);

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (!results.TryGetValue(i + 1, out var result))
            {
                priced.Add(NotPriced(line, "No pricing result"));
                continue;
            }

            if (result.RateSource == EngineDomain.RateSource.BilledCharges)
            {
                // No rate line in the named schedule.
                if (loaded.Schedule.Type == EngineDomain.FeeScheduleType.Drg)
                {
                    if (!drgNotFoundReported)
                        warnings.Add($"DRG {request.DrgCode} not found in fee schedule {request.FeeScheduleId}.");
                    drgNotFoundReported = true;
                    priced.Add(NotPriced(line, $"DRG {request.DrgCode} not found"));
                }
                else if (loaded.UnpricedReasons?.GetValueOrDefault(line.ProcedureCode ?? string.Empty) is { } unpricedReason)
                {
                    warnings.Add($"Line {line.LineNumber}: {unpricedReason}.");
                    priced.Add(NotPriced(line, unpricedReason));
                }
                else
                {
                    warnings.Add($"Line {line.LineNumber}: Code {line.ProcedureCode} not found in {request.FeeScheduleId}.");
                    priced.Add(NotPriced(line, $"Code {line.ProcedureCode} not found in fee schedule"));
                }
                continue;
            }

            if (result.RateSource == EngineDomain.RateSource.Unresolved)
            {
                var reason = result.UnresolvedReason ?? "Rate could not be determined";
                warnings.Add($"Line {line.LineNumber}: {reason}");
                priced.Add(NotPriced(line, reason));
                continue;
            }

            var multiProc = result.Adjustments.FirstOrDefault(a => a.Modifier == EngineDomain.PaymentModifiers.MultipleProcedures);
            if (multiProc is not null)
                warnings.Add($"Line {line.LineNumber}: Multiple procedure reduction applied ({multiProc.AdjustmentFactor:P0}).");
            foreach (var warning in result.Warnings)
                warnings.Add($"Line {line.LineNumber}: {warning}");
            if (line.Modifiers?.Any(m => string.Equals(m, "66", StringComparison.OrdinalIgnoreCase)) == true)
                warnings.Add($"Line {line.LineNumber}: Modifier 66 (team surgery) is priced by report; no adjustment applied.");

            priced.Add(new PricedLine
            {
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
                Modifiers = line.Modifiers,
                Units = line.Units,
                AllowedAmount = result.AllowedAmount,
                BilledAmount = line.BilledAmount,
                Breakdown = Breakdown(result, loaded, line, isFacility, multiProc),
                Status = PricingStatus.Priced,
                StatusReason = result.IsPerStayRate && perStayLines > 1
                    ? (result.FeeScheduleType == EngineDomain.FeeScheduleType.Drg
                        ? "Share of the DRG case rate, allocated by billed charges"
                        : "Share of the per diem, allocated by billed charges")
                    : null,
            });
        }

        return priced;
    }

    private static PricingBreakdown Breakdown(
        EngineModels.PricingResult result,
        LoadedPricingSchedule loaded,
        ClaimLineRequest line,
        bool isFacility,
        EngineModels.RateAdjustment? multiProc)
    {
        var schedule = loaded.Schedule;

        if (result.FeeScheduleType == EngineDomain.FeeScheduleType.Drg)
        {
            var drgLine = schedule.Lines.FirstOrDefault(l => string.Equals(l.ProcedureCode, result.ProcedureCode, StringComparison.OrdinalIgnoreCase))
                ?? schedule.Lines.FirstOrDefault();
            var weighted = drgLine?.DrgWeight is > 0m;
            return new PricingBreakdown
            {
                // The hospital base rate for a weighted DRG (the case rate is base × weight);
                // the flat case rate otherwise.
                BaseRate = weighted ? schedule.DrgBaseRate ?? drgLine!.Rate : result.BaseAmount,
                DrgRelativeWeight = drgLine?.DrgWeight,
                HospitalBaseRate = weighted ? schedule.DrgBaseRate ?? drgLine!.Rate : null,
            };
        }

        var rateLine = schedule.Lines.FirstOrDefault(l => string.Equals(l.ProcedureCode, line.ProcedureCode, StringComparison.OrdinalIgnoreCase));
        loaded.Details.TryGetValue(line.ProcedureCode ?? string.Empty, out var detail);

        // Modifier adjustments chain from the base amount; their sum gives the effective factor.
        var modifierTotal = result.Adjustments
            .Where(a => !string.IsNullOrEmpty(a.Modifier) && a.Modifier != EngineDomain.PaymentModifiers.MultipleProcedures)
            .Sum(a => a.AdjustmentAmount);
        decimal? modifierFactor = result.BaseAmount != 0m && modifierTotal != 0m
            ? Math.Round((result.BaseAmount + modifierTotal) / result.BaseAmount, 4)
            : null;

        return new PricingBreakdown
        {
            BaseRate = result.BaseAmount,
            FacilityIndicator = isFacility ? "Facility" : "Non-Facility",
            WorkRvu = rateLine?.WorkRvu,
            PracticeExpenseRvu = isFacility ? rateLine?.PeRvuFacility : rateLine?.PeRvu,
            MalpracticeRvu = rateLine?.MpRvu,
            ConversionFactor = detail?.ConversionFactor ?? schedule.ConversionFactor,
            MultiProcReduction = multiProc?.AdjustmentFactor,
            ModifierAdjustment = modifierFactor is { } f && f != 1m
                ? $"Factor: {f.ToString("0.####", CultureInfo.InvariantCulture)}"
                : null,
            ApcCode = detail?.ApcCode,
        };
    }

    private static PricedLine NotPriced(ClaimLineRequest line, string reason) => new()
    {
        LineNumber = line.LineNumber,
        ProcedureCode = line.ProcedureCode,
        Modifiers = line.Modifiers,
        Units = line.Units,
        AllowedAmount = 0,
        BilledAmount = line.BilledAmount,
        Breakdown = new PricingBreakdown(),
        Status = PricingStatus.NotFound,
        StatusReason = reason
    };
}
