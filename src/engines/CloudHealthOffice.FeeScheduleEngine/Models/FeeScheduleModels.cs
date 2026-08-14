using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.ReferenceData.Domain;

namespace CloudHealthOffice.FeeScheduleEngine.Models;

// ═══════════════════════════════════════════════════════════════════
// FEE SCHEDULE DOCUMENTS (persisted in Cosmos/Mongo)
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// A fee schedule — the lookup table of procedure code → allowed rate.
///
/// One schedule per payer/plan/year combination. Providers are linked to
/// schedules via ProviderContract.FeeScheduleId.
///
/// QNXT equivalent: FS_FEE_SCHEDULE + FS_FEE_SCHEDULE_LINE
/// </summary>
public class FeeSchedule
{
    /// <summary>Composite key: "{tenantId}:{name}:{effectiveDate:yyyyMMdd}"</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Human-readable name, e.g. "Medicare MPFS 2026 Locality 01"</summary>
    public string Name { get; set; } = string.Empty;

    public FeeScheduleType Type { get; set; }

    /// <summary>Import and licensing provenance; it does not affect rate calculation.</summary>
    public FeeScheduleSourceType SourceType { get; set; } = FeeScheduleSourceType.PayerContract;
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public string? NetworkId { get; set; }
    public string? Jurisdiction { get; set; }
    public string CodeSystem { get; set; } = "CPT";
    public string Checksum { get; set; } = string.Empty;
    public bool IsGlobal { get; set; }
    public LicenseClassification LicenseClassification { get; set; } = LicenseClassification.Unknown;

    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }

    /// <summary>
    /// CMS locality code for MPFS schedules (e.g. "01" = Alabama).
    /// Determines which GPCI values apply for RVU calculation.
    /// </summary>
    public string? Locality { get; set; }

    // ── MPFS / RVU fields ────────────────────────────────────────

    /// <summary>
    /// CMS conversion factor for the calendar year (e.g. 33.8872 for 2026).
    /// Required for FeeScheduleType.MedicareMpfs.
    /// AllowedAmount = RVU total × ConversionFactor.
    /// </summary>
    public decimal? ConversionFactor { get; set; }

    /// <summary>Work GPCI for this locality. Default 1.0 for non-locality-adjusted schedules.</summary>
    public decimal WorkGpci { get; set; } = 1.0m;

    /// <summary>Practice Expense GPCI.</summary>
    public decimal PeGpci { get; set; } = 1.0m;

    /// <summary>Malpractice GPCI.</summary>
    public decimal MpGpci { get; set; } = 1.0m;

    // ── Medicaid / percent-of-Medicare ──────────────────────────

    /// <summary>
    /// For FeeScheduleType.Medicaid: multiplier applied to the Medicare MPFS rate.
    /// E.g. 0.72 = 72% of Medicare.
    /// </summary>
    public decimal? PercentOfMedicare { get; set; }

    /// <summary>
    /// For Medicaid schedules: the MPFS fee schedule ID to use as the base rate.
    /// If null, lines must store pre-calculated flat rates.
    /// </summary>
    public string? BaseMpfsFeeScheduleId { get; set; }

    /// <summary>
    /// For FeeScheduleType.PerDiem: daily rate (AllowedAmount = PerDiemRate × LengthOfStay).
    /// </summary>
    public decimal? PerDiemRate { get; set; }

    /// <summary>
    /// For FeeScheduleType.Drg: base rate used with DRG relative weights.
    /// AllowedAmount = DrgBaseRate × FeeScheduleLine.DrgWeight.
    /// If null, each DRG line stores its own flat case rate in Line.Rate.
    /// </summary>
    public decimal? DrgBaseRate { get; set; }

    /// <summary>The procedure/service lines for this schedule.</summary>
    public List<FeeScheduleLine> Lines { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    public static string MakeId(string tenantId, string name, DateTime effectiveDate)
        => $"{tenantId}:{name}:{effectiveDate:yyyyMMdd}";
}

/// <summary>
/// One procedure code → rate mapping within a fee schedule.
///
/// For MPFS schedules, WorkRvu/PeRvu/MpRvu are stored here and
/// rate is computed at runtime using the schedule's GPCI × ConversionFactor.
/// For all other types, Rate is the pre-calculated flat dollar amount.
/// </summary>
public class FeeScheduleLine
{
    /// <summary>CPT/HCPCS procedure code.</summary>
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Optional modifier qualifier. If set, this line applies only when
    /// the claim line has this modifier (e.g. "26" for professional component,
    /// "TC" for technical component). Null = base rate (no modifier).
    /// </summary>
    public string? Modifier { get; set; }

    public FeeScheduleRateType RateType { get; set; }

    /// <summary>
    /// Base rate amount. Interpretation depends on RateType:
    ///   FlatRate         → dollar amount per unit
    ///   PercentOfBilled  → multiplier (e.g. 0.80 = 80% of billed charges)
    ///   PercentOfMedicare→ multiplier (e.g. 1.10 = 110% of Medicare rate)
    ///   Rvu              → not used here; rate calculated from RVU fields below
    /// </summary>
    public decimal Rate { get; set; }

    // ── MPFS RVU components (RateType == Rvu only) ──────────────

    /// <summary>Work RVU (physician effort, skill, time).</summary>
    public decimal? WorkRvu { get; set; }

    /// <summary>Practice Expense RVU (facility or non-facility).</summary>
    public decimal? PeRvu { get; set; }

    /// <summary>Malpractice RVU.</summary>
    public decimal? MpRvu { get; set; }

    /// <summary>
    /// Whether this is a non-facility PE RVU (office/outpatient) or facility PE RVU.
    /// CMS publishes both; the engine selects based on PlaceOfService.
    /// </summary>
    public decimal? PeRvuFacility { get; set; }

    // ── DRG fields ────────────────────────────────────────────

    /// <summary>
    /// DRG relative weight. When the schedule is FeeScheduleType.Drg,
    /// allowed amount = DrgBaseRate × DrgWeight. If null, Rate is the
    /// flat case rate for this DRG code.
    /// </summary>
    public decimal? DrgWeight { get; set; }

    // ── Limits ──────────────────────────────────────────────────

    /// <summary>Maximum billable units per day (MUE equivalent for pricing).</summary>
    public decimal? MaxUnitsPerDay { get; set; }

    /// <summary>When true, bilateral modifier (50) applies the 150% adjustment.</summary>
    public bool BilateralAdjustmentApplies { get; set; } = true;

    /// <summary>When true, multiple procedure reduction (51) applies for secondary procedures.</summary>
    public bool MultipleProcedureReductionApplies { get; set; } = true;

    /// <summary>
    /// Assistant-at-surgery allowed (if false, assistant modifier claims price at $0).
    /// </summary>
    public bool AssistantAtSurgeryAllowed { get; set; } = true;
}

// ═══════════════════════════════════════════════════════════════════
// PROVIDER CONTRACT
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Links a provider to a fee schedule for a specific plan.
/// Multiple contracts can exist for one provider across different plans or date ranges.
///
/// QNXT equivalent: CONTRACT + CONTRACT_LINE + PROV_PLAN
/// </summary>
public class ProviderContract
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Rendering or billing provider NPI.</summary>
    public string ProviderNpi { get; set; } = string.Empty;

    /// <summary>Group/organization TIN (optional; used when NPI lookup fails).</summary>
    public string? GroupTin { get; set; }

    /// <summary>The benefit plan this contract applies to.</summary>
    public string PlanId { get; set; } = string.Empty;

    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }

    public NetworkStatus NetworkStatus { get; set; }

    /// <summary>
    /// Default fee schedule for this provider/plan combination.
    /// Applies to all service categories unless overridden by ContractLines.
    /// </summary>
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>
    /// Service-category-specific fee schedule overrides.
    /// E.g. a provider may have a separate schedule for mental health or DME.
    /// </summary>
    public List<ProviderContractLine> ContractLines { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    public static string MakeId(string tenantId, string providerNpi, string planId)
        => $"{tenantId}:{providerNpi}:{planId}";
}

/// <summary>
/// Service-category-specific fee schedule override within a provider contract.
/// E.g. use schedule "MENTAL-HEALTH-2026" for procedure codes in range 90785–90899.
/// </summary>
public class ProviderContractLine
{
    /// <summary>CPT/HCPCS range start (inclusive). Null = all procedures.</summary>
    public string? ProcedureCodeFrom { get; set; }

    /// <summary>CPT/HCPCS range end (inclusive). Null = single code or open range.</summary>
    public string? ProcedureCodeTo { get; set; }

    /// <summary>Override fee schedule for this service category.</summary>
    public string FeeScheduleId { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════
// PRICING REQUEST / RESULT
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Input to the rate resolution engine for a single claim line.
/// </summary>
public record PricingRequest
{
    public string TenantId { get; init; } = string.Empty;

    /// <summary>CPT/HCPCS procedure code.</summary>
    public string ProcedureCode { get; init; } = string.Empty;

    /// <summary>Procedure modifiers from the 837 SV101-3/4/5/6 or SV201-3/4.</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();

    /// <summary>Rendering provider NPI (preferred) or billing provider NPI.</summary>
    public string ProviderNpi { get; init; } = string.Empty;

    /// <summary>Place of service code (affects facility vs non-facility PE RVUs).</summary>
    public string PlaceOfServiceCode { get; init; } = "11";

    public DateTime ServiceDate { get; init; }

    /// <summary>Benefit plan ID — used to find the provider's contracted schedule.</summary>
    public string PlanId { get; init; } = string.Empty;

    /// <summary>Provider's billed charge for this line (used for UCR / PercentOfBilled).</summary>
    public decimal BilledAmount { get; init; }

    /// <summary>Units billed on this service line.</summary>
    public decimal Units { get; init; } = 1;

    // ── Multiple procedure context ───────────────────────────────

    /// <summary>
    /// Line number within the claim (1-based). Determines multiple-procedure
    /// reduction rank: line 1 = 100%, lines 2–N = 50%.
    /// </summary>
    public int LineNumber { get; init; } = 1;

    /// <summary>Total number of lines on the claim (enables multiple procedure detection).</summary>
    public int TotalLineCount { get; init; } = 1;

    // ── Inpatient fields ─────────────────────────────────────────

    /// <summary>Length of stay in days (required for PerDiem and DRG schedules).</summary>
    public int? LengthOfStay { get; init; }

    /// <summary>DRG code (required for FeeScheduleType.Drg).</summary>
    public string? DrgCode { get; init; }
}

/// <summary>
/// Output of the rate resolution engine for one claim line.
/// </summary>
public record PricingResult
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = string.Empty;

    /// <summary>Final allowed amount after all adjustments. Zero for capitation.</summary>
    public decimal AllowedAmount { get; init; }

    public decimal BilledAmount { get; init; }

    /// <summary>BilledAmount − AllowedAmount. Written as CO-45 CAS on the 835.</summary>
    public decimal ContractualAdjustment => BilledAmount - AllowedAmount;

    public FeeScheduleType FeeScheduleType { get; init; }
    public RateSource RateSource { get; init; }
    public NetworkStatus NetworkStatus { get; init; }

    /// <summary>ID of the matched fee schedule (for audit).</summary>
    public string? FeeScheduleId { get; init; }

    /// <summary>Name of the matched fee schedule (for portal display).</summary>
    public string? FeeScheduleName { get; init; }

    /// <summary>Ordered list of adjustments applied to arrive at AllowedAmount.</summary>
    public IReadOnlyList<RateAdjustment> Adjustments { get; init; } = Array.Empty<RateAdjustment>();
}

/// <summary>
/// One modifier or rule adjustment applied during rate resolution.
/// These populate the CAS segments on the 835 ERA.
/// </summary>
public record RateAdjustment
{
    /// <summary>Modifier code that triggered this adjustment (e.g. "50", "51", "26").</summary>
    public string Modifier { get; init; } = string.Empty;

    /// <summary>Human-readable description for audit/portal.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Multiplicative factor applied (e.g. 0.50 for multiple procedure reduction).</summary>
    public decimal AdjustmentFactor { get; init; }

    /// <summary>Dollar amount of the adjustment (negative = reduction).</summary>
    public decimal AdjustmentAmount { get; init; }
}

/// <summary>
/// Batch pricing result — one entry per claim line.
/// </summary>
public record PricingResultSet
{
    public IReadOnlyList<PricingResult> LineResults { get; init; } = Array.Empty<PricingResult>();

    public decimal TotalAllowedAmount => LineResults.Sum(r => r.AllowedAmount);
    public decimal TotalBilledAmount  => LineResults.Sum(r => r.BilledAmount);
    public decimal TotalContractualAdjustment => LineResults.Sum(r => r.ContractualAdjustment);
}
