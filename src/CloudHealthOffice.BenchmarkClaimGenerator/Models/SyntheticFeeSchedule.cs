namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a fee schedule with rate entries for procedure code pricing.
/// Structurally compatible with the production FeeSchedule entity in FeeScheduleEngine.
/// </summary>
public class SyntheticFeeSchedule
{
    /// <summary>Unique fee schedule identifier (e.g., FS-MEDICAID, FS-OON, FS-CAPITATION).</summary>
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>Tenant identifier.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Fee schedule type: Medicaid, Commercial, Custom, PerDiem, Drg, Capitation.</summary>
    public string Type { get; set; } = "Medicaid";

    /// <summary>Effective date.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Termination date (null if active).</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>Percent of Medicare rate (e.g., 0.72 = 72% of Medicare). Used for Medicaid schedules.</summary>
    public decimal? PercentOfMedicare { get; set; }

    /// <summary>DRG base rate for inpatient pricing.</summary>
    public decimal? DrgBaseRate { get; set; }

    /// <summary>Per diem rate for per-diem contracts.</summary>
    public decimal? PerDiemRate { get; set; }

    /// <summary>Rate entries indexed by procedure code.</summary>
    public List<SyntheticFeeScheduleLine> Lines { get; set; } = new();

    /// <summary>DRG rate entries for inpatient pricing.</summary>
    public List<SyntheticDrgRate> DrgRates { get; set; } = new();

    /// <summary>Capitation PMPM rates by LOB/program.</summary>
    public List<SyntheticCapitationRate> CapitationRates { get; set; } = new();
}

/// <summary>
/// A single rate entry in a fee schedule for a procedure code.
/// </summary>
public class SyntheticFeeScheduleLine
{
    /// <summary>CPT/HCPCS/CDT procedure code.</summary>
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>Optional modifier qualifier (26, TC, etc.).</summary>
    public string? Modifier { get; set; }

    /// <summary>Place of service code (11=Office, 21=Inpatient, etc.).</summary>
    public string? PlaceOfService { get; set; }

    /// <summary>Allowed amount for this procedure.</summary>
    public decimal AllowedAmount { get; set; }

    /// <summary>Rate type: FlatRate, Rvu, PercentOfBilled, PercentOfMedicare.</summary>
    public string RateType { get; set; } = "FlatRate";

    /// <summary>Effective date for this line.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Term date for this line (null if active).</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>Maximum units per day.</summary>
    public decimal? MaxUnitsPerDay { get; set; }

    /// <summary>Whether bilateral adjustment (150%) applies.</summary>
    public bool BilateralAdjustmentApplies { get; set; } = true;

    /// <summary>Whether multiple procedure reduction applies.</summary>
    public bool MultipleProcedureReductionApplies { get; set; } = true;
}

/// <summary>
/// A DRG rate entry for inpatient pricing.
/// </summary>
public class SyntheticDrgRate
{
    /// <summary>DRG code.</summary>
    public string DrgCode { get; set; } = string.Empty;

    /// <summary>DRG description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>DRG relative weight.</summary>
    public decimal Weight { get; set; } = 1.0m;

    /// <summary>Allowed amount for this DRG (BaseRate × Weight).</summary>
    public decimal AllowedAmount { get; set; }
}

/// <summary>
/// Capitation per-member-per-month rate by program.
/// </summary>
public class SyntheticCapitationRate
{
    /// <summary>Medicaid program or LOB name (STAR, CHIP, STAR+PLUS, etc.).</summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>Age range description (e.g., "Adult", "Child").</summary>
    public string? AgeRange { get; set; }

    /// <summary>Per-member-per-month rate.</summary>
    public decimal PmpmRate { get; set; }
}
