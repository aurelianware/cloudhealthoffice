using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;
using EnginePersistence = CloudHealthOffice.FeeScheduleEngine.Persistence;

namespace CloudHealthOffice.PricingApi.Services.Engine;

/// <summary>What one repricing request needs from a fee schedule.</summary>
public sealed record PricingScheduleQuery(
    ClaimType ClaimType,
    string? Locality,
    IReadOnlyCollection<string> ProcedureCodes,
    string? DrgCode);

/// <summary>Display-only detail the engine schedule does not carry (APC code, conversion factor).</summary>
public sealed record PricingLineDetail(string? ApcCode, decimal? ConversionFactor);

/// <summary>
/// A fee schedule in the engine's model (<see cref="EngineModels.FeeSchedule"/>), ready for
/// <c>RateResolutionService</c>, plus the version label the Pricing API reports.
/// </summary>
public sealed record LoadedPricingSchedule(
    EngineModels.FeeSchedule Schedule,
    string Version,
    IReadOnlyDictionary<string, PricingLineDetail> Details);

/// <summary>
/// Where the Pricing API reads fee schedules from. Repricing always runs on the
/// shared rate resolution engine (ADR 014); a source only supplies the schedule
/// in the engine's model.
///
/// <list type="bullet">
///   <item><see cref="LegacyEntryScheduleSource"/> — the Pricing API's own
///   <c>fee_schedule_entries</c> store, adapted line by line. Registered today.</item>
///   <item><see cref="EngineStoreScheduleSource"/> — the canonical fee schedule
///   store claims adjudication reads. Wired by the dual-read step of the ADR 014
///   migration; used now by the parity tests.</item>
/// </list>
/// </summary>
public interface IPricingScheduleSource
{
    /// <summary>The requested schedule, restricted to what the claim needs where the store allows; null when it does not exist.</summary>
    Task<LoadedPricingSchedule?> LoadAsync(string feeScheduleId, PricingScheduleQuery query, CancellationToken ct = default);

    /// <summary>A schedule another schedule references (the Medicare base of a percent-of-Medicare line); null when unavailable.</summary>
    Task<EngineModels.FeeSchedule?> GetReferencedAsync(string feeScheduleId, CancellationToken ct = default);
}

/// <summary>
/// Adapts the Pricing API's legacy <c>fee_schedule_entries</c> rows to the engine's
/// schedule model. Only the rows for the claim's codes (or its DRG) are loaded.
///
/// <para>Mapping (lossless for what the legacy store can express):</para>
/// <list type="bullet">
///   <item>Professional / outpatient entry → flat-rate line: <c>Rate</c> = non-facility
///   price (APC payment rate when absent), <c>FacilityRate</c> = facility price (APC
///   payment rate when absent). The engine picks one by place of service. RVUs are
///   carried for display only.</item>
///   <item>MPFS multiple procedure indicator → the engine's indicator. An OPPS entry has
///   none: status indicator T (multiple procedure discount) maps to 2, any other
///   status indicator to 9 (concept does not apply).</item>
///   <item>Inpatient claim → DRG schedule with the claim's DRG row: <c>Rate</c> = base
///   rate, <c>DrgWeight</c> = relative weight (case rate = base × weight).</item>
///   <item>Schedule type: RBRVS → MedicareMpfs, OPPS → MedicareOpps, Medicaid → Medicaid,
///   Commercial → Commercial.</item>
/// </list>
/// The legacy store cannot express percent-of-Medicare, per diem or contract terms;
/// those price only from the canonical store (<see cref="EngineStoreScheduleSource"/>).
/// </summary>
public sealed class LegacyEntryScheduleSource : IPricingScheduleSource
{
    private readonly IFeeScheduleRepository _repo;

    public LegacyEntryScheduleSource(IFeeScheduleRepository repo) => _repo = repo;

    public async Task<LoadedPricingSchedule?> LoadAsync(
        string feeScheduleId, PricingScheduleQuery query, CancellationToken ct = default)
    {
        var info = await _repo.GetScheduleInfoAsync(feeScheduleId);
        if (info is null)
            return null;

        var schedule = new EngineModels.FeeSchedule
        {
            Id = feeScheduleId,
            TenantId = string.Empty,
            Name = info.Name,
            SourceVersion = info.Version,
            EffectiveDate = info.EffectiveDate.ToDateTime(TimeOnly.MinValue),
            TermDate = info.TermDate?.ToDateTime(TimeOnly.MinValue),
        };
        var details = new Dictionary<string, PricingLineDetail>(StringComparer.OrdinalIgnoreCase);

        if (query.ClaimType == ClaimType.Inpatient)
        {
            schedule.Type = EngineDomain.FeeScheduleType.Drg;
            if (!string.IsNullOrWhiteSpace(query.DrgCode)
                && await _repo.LookupDrgAsync(feeScheduleId, query.DrgCode) is { } drg)
            {
                schedule.Lines.Add(ToDrgLine(drg));
            }
        }
        else
        {
            schedule.Type = ToEngineType(info.Type);
            var codes = query.ProcedureCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var entries = codes.Count == 0
                ? []
                : await _repo.LookupCodesAsync(feeScheduleId, codes, query.Locality);

            // Without a locality a code may have one row per locality; the first
            // wins, as in the single-code lookup.
            foreach (var entry in entries.GroupBy(e => e.ProcedureCode, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                schedule.Lines.Add(ToLine(entry));
                details[entry.ProcedureCode] = new PricingLineDetail(entry.ApcCode, entry.ConversionFactor);
            }
        }

        return new LoadedPricingSchedule(schedule, info.Version, details);
    }

    /// <summary>The legacy store holds no cross-schedule references.</summary>
    public Task<EngineModels.FeeSchedule?> GetReferencedAsync(string feeScheduleId, CancellationToken ct = default)
        => Task.FromResult<EngineModels.FeeSchedule?>(null);

    internal static EngineDomain.FeeScheduleType ToEngineType(FeeScheduleType type) => type switch
    {
        FeeScheduleType.MedicareRbrvs => EngineDomain.FeeScheduleType.MedicareMpfs,
        FeeScheduleType.MedicareOpps => EngineDomain.FeeScheduleType.MedicareOpps,
        FeeScheduleType.Medicaid => EngineDomain.FeeScheduleType.Medicaid,
        FeeScheduleType.Commercial => EngineDomain.FeeScheduleType.Commercial,
        // A DRG schedule priced line by line (non-inpatient claim): plain flat rates.
        _ => EngineDomain.FeeScheduleType.Custom,
    };

    internal static EngineModels.FeeScheduleLine ToLine(FeeScheduleEntry entry) => new()
    {
        ProcedureCode = entry.ProcedureCode,
        RateType = EngineDomain.FeeScheduleRateType.FlatRate,
        Rate = entry.NonFacilityRate ?? entry.ApcPaymentRate ?? 0m,
        FacilityRate = entry.FacilityRate ?? entry.ApcPaymentRate ?? 0m,
        WorkRvu = entry.WorkRvu,
        PeRvu = entry.PracticeExpenseRvu,
        PeRvuFacility = entry.PracticeExpenseRvuFacility,
        MpRvu = entry.MalpracticeRvu,
        MultipleProcedureIndicator = ToIndicator(entry),
    };

    internal static EngineModels.FeeScheduleLine ToDrgLine(FeeScheduleEntry entry) => entry.DrgWeight is > 0m
        ? new()
        {
            ProcedureCode = entry.ProcedureCode,
            RateType = EngineDomain.FeeScheduleRateType.FlatRate,
            Rate = entry.DrgBaseRate ?? 0m,
            DrgWeight = entry.DrgWeight,
        }
        : new()
        {
            // No weight: the stored price is the case rate.
            ProcedureCode = entry.ProcedureCode,
            RateType = EngineDomain.FeeScheduleRateType.FlatRate,
            Rate = entry.NonFacilityRate ?? 0m,
        };

    private static EngineDomain.MultipleProcedureIndicator? ToIndicator(FeeScheduleEntry entry)
    {
        if (entry.MultipleProcedureIndicator is { } indicator)
            return indicator is >= 0 and <= byte.MaxValue ? (EngineDomain.MultipleProcedureIndicator)(byte)indicator : null;

        if (!string.IsNullOrEmpty(entry.StatusIndicator))
            return string.Equals(entry.StatusIndicator, "T", StringComparison.OrdinalIgnoreCase)
                ? EngineDomain.MultipleProcedureIndicator.StandardSurgery
                : EngineDomain.MultipleProcedureIndicator.NotApplicable;

        return entry.DrgWeight is not null ? EngineDomain.MultipleProcedureIndicator.NotApplicable : null;
    }
}

/// <summary>
/// Reads fee schedules from the canonical store — the engine's own
/// <see cref="EnginePersistence.IFeeScheduleRepository"/>, the store claims
/// adjudication prices from — for one tenant. Schedules are returned whole, as
/// adjudication sees them, so every rate type (percent-of-Medicare, per diem,
/// DRG, RVU) prices the same way through both entry points.
/// </summary>
public sealed class EngineStoreScheduleSource : IPricingScheduleSource
{
    private readonly EnginePersistence.IFeeScheduleRepository _repo;
    private readonly string _tenantId;

    public EngineStoreScheduleSource(EnginePersistence.IFeeScheduleRepository repo, string tenantId)
    {
        _repo = repo;
        _tenantId = tenantId;
    }

    public async Task<LoadedPricingSchedule?> LoadAsync(
        string feeScheduleId, PricingScheduleQuery query, CancellationToken ct = default)
    {
        var schedule = await _repo.GetByIdAsync(_tenantId, feeScheduleId, ct);
        if (schedule is null)
            return null;

        var version = string.IsNullOrEmpty(schedule.SourceVersion)
            ? schedule.EffectiveDate.ToString("yyyy-MM-dd")
            : schedule.SourceVersion;
        return new LoadedPricingSchedule(schedule, version, new Dictionary<string, PricingLineDetail>());
    }

    public Task<EngineModels.FeeSchedule?> GetReferencedAsync(string feeScheduleId, CancellationToken ct = default)
        => _repo.GetByIdAsync(_tenantId, feeScheduleId, ct);
}
