using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.ReferenceData.Domain;
using MongoDB.Bson.Serialization.Attributes;

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
    /// The Medicare MPFS fee schedule ID to use as the base rate for Medicaid
    /// schedules and for PercentOfMedicare lines on Commercial/Custom schedules.
    /// If null, lines must store pre-calculated flat rates (or inline RVUs).
    /// </summary>
    public string? BaseMpfsFeeScheduleId { get; set; }

    /// <summary>
    /// For FeeScheduleType.PerDiem: all-inclusive daily rate for the stay
    /// (AllowedAmount = PerDiemRate × LengthOfStay, paid once per claim — see
    /// <c>RateResolutionService.ResolveBatchAsync</c>). When null, each
    /// matched schedule line is a line-level daily rate (Line.Rate × Units).
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
///
/// Extra elements are ignored so documents written before
/// MultipleProcedureReductionApplies became derived still deserialize.
/// </summary>
[BsonIgnoreExtraElements]
public class FeeScheduleLine
{
    /// <summary>CPT/HCPCS procedure code.</summary>
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Optional UB-04 revenue code (e.g. "0120" semi-private room and board).
    /// A line with a revenue code and no <see cref="ProcedureCode"/> prices
    /// institutional claim lines billed with that revenue code when no
    /// procedure-code line matched — the usual shape for room-and-board
    /// per diem and flat-rate revenue-code contracts.
    /// </summary>
    public string? RevenueCode { get; set; }

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

    /// <summary>
    /// Optional facility price for a <see cref="FeeScheduleRateType.FlatRate"/> line
    /// (the CMS MPFS "facility price" column). When set, it replaces <see cref="Rate"/>
    /// for claim lines rendered in a facility place of service (see
    /// <see cref="FacilityPlaceOfService"/>); <see cref="Rate"/> is then
    /// the non-facility price. Null = one price for every place of service.
    /// </summary>
    public decimal? FacilityRate { get; set; }

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

    /// <summary>
    /// CMS MPFS multiple procedure indicator (PPRRVU "MULT PROC" column).
    /// Null = not supplied by the source data; the engine then applies no
    /// reduction and flags the line on the pricing result.
    /// </summary>
    public MultipleProcedureIndicator? MultipleProcedureIndicator { get; set; }

    /// <summary>
    /// True only for indicator 2 (standard multiple surgery), the only rule the
    /// engine's 100/50/50 ranking implements. Derived from
    /// <see cref="MultipleProcedureIndicator"/>; not persisted.
    /// </summary>
    [JsonIgnore]
    [BsonIgnore]
    public bool MultipleProcedureReductionApplies
        => MultipleProcedureIndicator == Domain.MultipleProcedureIndicator.StandardSurgery;

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

    /// <summary>
    /// Lesser-of-billed provision. When true, each line's allowed amount is the
    /// lesser of the contract rate (after modifier and multiple procedure
    /// adjustments) and the provider's billed charge; a per-stay rate (DRG case
    /// rate, all-inclusive per diem) is compared once with the stay's total billed
    /// charges. Default false: the contract rate is paid even when it exceeds billed.
    /// </summary>
    public bool LesserOfBilledCharges { get; set; }

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

    /// <summary>
    /// DRG code (required for FeeScheduleType.Drg). Must be the DRG billed on
    /// the claim (837I HI*DR) — the engine does not group. Send the same code
    /// on every line of the claim: batch pricing pays the DRG case rate once,
    /// allocated across the lines in proportion to their billed charges.
    /// </summary>
    public string? DrgCode { get; init; }

    /// <summary>
    /// UB-04 revenue code (837I SV201). Used to match revenue-code fee
    /// schedule lines when no procedure-code line matches.
    /// </summary>
    public string? RevenueCode { get; init; }

    /// <summary>
    /// Three-character type of bill (837I CLM05-1 facility type + CLM05-3
    /// frequency; "0111" is also accepted). A <b>valid</b> type of bill
    /// (<see cref="CloudHealthOffice.ReferenceData.Domain.NubcTypeOfBill"/>) marks the
    /// line as institutional, which always takes the facility rate
    /// (<see cref="PlaceOfServiceCode"/> then holds the facility type code, not a CMS
    /// place of service). A malformed value ("0", "N/A") is ignored for the facility
    /// decision; the value is otherwise carried for audit.
    /// </summary>
    public string? BillType { get; init; }

    /// <summary>
    /// True when the claim is institutional (837I / ClaimType Institutional),
    /// whether or not a valid <see cref="BillType"/> could be built. An institutional
    /// line always takes the facility rate, because its
    /// <see cref="PlaceOfServiceCode"/> holds the CLM05-1 facility type code (e.g.
    /// "13"), not a CMS place of service. Callers set it from the claim type.
    /// </summary>
    public bool IsInstitutional { get; init; }
}

/// <summary>
/// Output of the rate resolution engine for one claim line.
/// </summary>
public record PricingResult
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = string.Empty;

    /// <summary>Final allowed amount after all adjustments, rounded to cents. Zero for capitation.</summary>
    public decimal AllowedAmount { get; init; }

    /// <summary>
    /// Base amount the rate line produced before modifier adjustments, units,
    /// multiple procedure reduction, per-stay allocation and lesser-of: per unit
    /// for unit-priced lines; the line total for billed-charge based amounts and
    /// per diem × length of stay. For audit and display.
    /// </summary>
    public decimal BaseAmount { get; init; }

    /// <summary>
    /// True when the contract's lesser-of-billed provision lowered the allowed
    /// amount to the billed charge (see <see cref="ProviderContract.LesserOfBilledCharges"/>).
    /// </summary>
    public bool LesserOfBilledApplied { get; init; }

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

    /// <summary>
    /// True when this line's amount is its share of a claim-level per-stay
    /// rate — a DRG case rate or an all-inclusive per diem × length of stay —
    /// paid once per claim and allocated across the lines by billed charges.
    /// Benefit calculation should then apply cost sharing once per stay.
    /// </summary>
    public bool IsPerStayRate { get; init; }

    /// <summary>
    /// Set when <see cref="RateSource"/> is <see cref="RateSource.Unresolved"/>:
    /// why the allowed amount could not be determined.
    /// </summary>
    public string? UnresolvedReason { get; init; }

    /// <summary>
    /// Non-fatal pricing notes that need visibility but do not change the allowed
    /// amount — e.g. the rate line has no CMS multiple procedure indicator, or its
    /// indicator names a reduction rule the engine does not yet implement.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
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
