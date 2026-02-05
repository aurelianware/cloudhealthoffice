using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ReferenceDataService.Models;

/// <summary>
/// CPT (Current Procedural Terminology) code
/// Medical procedures and services (maintained by AMA)
/// ~44,000 codes total
/// </summary>
[Table("cpt_codes")]
public class CptCode
{
    /// <summary>
    /// CPT code (5 digits, or 4 digits + 1 letter for Category III)
    /// Examples: 99213 (Office visit), 70450 (CT brain), 0001U (Genetic test)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(5)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Short description (for display)
    /// </summary>
    [Column("short_description")]
    [StringLength(200)]
    public string ShortDescription { get; set; } = string.Empty;

    /// <summary>
    /// Long description (full clinical definition)
    /// </summary>
    [Column("long_description")]
    [StringLength(1000)]
    public string? LongDescription { get; set; }

    /// <summary>
    /// CPT category
    /// Category I = Established procedures (5-digit numeric)
    /// Category II = Performance measurement (4 digits + F)
    /// Category III = Emerging technology (4 digits + T or U)
    /// </summary>
    [Column("category")]
    [StringLength(20)]
    public string Category { get; set; } = "Category I";

    /// <summary>
    /// Section (e.g., "Surgery", "Medicine", "Evaluation and Management")
    /// </summary>
    [Column("section")]
    [StringLength(100)]
    public string? Section { get; set; }

    /// <summary>
    /// Subsection (more specific grouping)
    /// </summary>
    [Column("subsection")]
    [StringLength(100)]
    public string? Subsection { get; set; }

    /// <summary>
    /// Modifier exempt (true = modifiers not allowed)
    /// </summary>
    [Column("modifier_exempt")]
    public bool ModifierExempt { get; set; }

    /// <summary>
    /// Status code
    /// A = Active, D = Deleted, R = Reinstated
    /// </summary>
    [Column("status_code")]
    [StringLength(1)]
    public string StatusCode { get; set; } = "A";

    /// <summary>
    /// Effective date
    /// </summary>
    [Column("effective_date")]
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// End date (if deleted)
    /// </summary>
    [Column("end_date")]
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Requires prior authorization (common flag for high-cost procedures)
    /// </summary>
    [Column("requires_prior_auth")]
    public bool RequiresPriorAuth { get; set; }

    /// <summary>
    /// Average Medicare payment (for reference)
    /// </summary>
    [Column("medicare_payment")]
    public decimal? MedicarePayment { get; set; }
}

/// <summary>
/// ICD-10-CM (International Classification of Diseases, 10th Revision, Clinical Modification)
/// Diagnosis codes (~70,000 codes)
/// </summary>
[Table("icd10_codes")]
public class Icd10Code
{
    /// <summary>
    /// ICD-10 code (3-7 characters, alphanumeric)
    /// Examples: E11.9 (Type 2 diabetes), I10 (Essential hypertension), S72.001A (Femur fracture)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Short description (for display)
    /// </summary>
    [Column("short_description")]
    [StringLength(200)]
    public string ShortDescription { get; set; } = string.Empty;

    /// <summary>
    /// Long description (full clinical definition)
    /// </summary>
    [Column("long_description")]
    [StringLength(1000)]
    public string? LongDescription { get; set; }

    /// <summary>
    /// Category chapter (e.g., "Endocrine", "Circulatory", "Injury")
    /// </summary>
    [Column("category_chapter")]
    [StringLength(100)]
    public string? CategoryChapter { get; set; }

    /// <summary>
    /// Billable (true = can be used on claims, false = header/category only)
    /// </summary>
    [Column("billable")]
    public bool Billable { get; set; } = true;

    /// <summary>
    /// Seventh character required (for injury/trauma codes)
    /// </summary>
    [Column("seventh_char_required")]
    public bool SeventhCharRequired { get; set; }

    /// <summary>
    /// Valid seventh characters (A, D, S for initial, subsequent, sequela)
    /// </summary>
    [Column("valid_seventh_chars")]
    [StringLength(50)]
    public string? ValidSeventhChars { get; set; }

    /// <summary>
    /// Laterality required (left vs right)
    /// </summary>
    [Column("laterality_required")]
    public bool LateralityRequired { get; set; }

    /// <summary>
    /// Status code
    /// A = Active, D = Deleted, R = Revised
    /// </summary>
    [Column("status_code")]
    [StringLength(1)]
    public string StatusCode { get; set; } = "A";

    /// <summary>
    /// Effective date
    /// </summary>
    [Column("effective_date")]
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// End date (if deleted)
    /// </summary>
    [Column("end_date")]
    public DateTime? EndDate { get; set; }
}

/// <summary>
/// HCPCS Level II codes (Healthcare Common Procedure Coding System)
/// Non-physician services, DME, drugs, supplies (~8,000 codes)
/// </summary>
[Table("hcpcs_codes")]
public class HcpcsCode
{
    /// <summary>
    /// HCPCS code (1 letter + 4 digits)
    /// Examples: J0885 (Drug injection), E0100 (Walker), A4253 (Blood glucose strips)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(5)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Short description (for display)
    /// </summary>
    [Column("short_description")]
    [StringLength(200)]
    public string ShortDescription { get; set; } = string.Empty;

    /// <summary>
    /// Long description (full definition)
    /// </summary>
    [Column("long_description")]
    [StringLength(1000)]
    public string? LongDescription { get; set; }

    /// <summary>
    /// Category (Drugs, DME, Medical Supplies, etc.)
    /// </summary>
    [Column("category")]
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>
    /// Status code
    /// A = Active, D = Deleted, T = Terminated
    /// </summary>
    [Column("status_code")]
    [StringLength(1)]
    public string StatusCode { get; set; } = "A";

    /// <summary>
    /// Coverage level
    /// C = Carrier discretion, N = Non-covered, E = Special coverage
    /// </summary>
    [Column("coverage_level")]
    [StringLength(1)]
    public string? CoverageLevel { get; set; }

    /// <summary>
    /// Effective date
    /// </summary>
    [Column("effective_date")]
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// End date (if deleted)
    /// </summary>
    [Column("end_date")]
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Average Medicare payment
    /// </summary>
    [Column("medicare_payment")]
    public decimal? MedicarePayment { get; set; }
}

/// <summary>
/// CPT Modifier codes (2-character codes that modify procedure billing)
/// Examples: 50 = Bilateral, 22 = Increased procedural services, 59 = Distinct procedural service
/// </summary>
[Table("modifiers")]
public class Modifier
{
    /// <summary>
    /// Modifier code (2 characters, numeric or alphanumeric)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(2)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Description
    /// </summary>
    [Column("description")]
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category (Anesthesia, Surgery, E/M, etc.)
    /// </summary>
    [Column("category")]
    [StringLength(50)]
    public string? Category { get; set; }

    /// <summary>
    /// Price impact (multiplier: 1.0 = no change, 0.5 = 50% payment, 1.5 = 150% payment)
    /// </summary>
    [Column("price_impact")]
    public decimal PriceImpact { get; set; } = 1.0m;

    /// <summary>
    /// Status (A = Active, D = Deleted)
    /// </summary>
    [Column("status")]
    [StringLength(1)]
    public string Status { get; set; } = "A";
}

/// <summary>
/// DRG (Diagnosis Related Group) codes for inpatient hospital payment
/// MS-DRG (Medicare Severity) - ~750 codes
/// </summary>
[Table("drg_codes")]
public class DrgCode
{
    /// <summary>
    /// DRG code (3 digits)
    /// Examples: 470 = Major joint replacement, 291 = Heart failure
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(3)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Description
    /// </summary>
    [Column("description")]
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// MDC (Major Diagnostic Category) - body system grouping
    /// </summary>
    [Column("mdc")]
    [StringLength(2)]
    public string? MDC { get; set; }

    /// <summary>
    /// MDC description
    /// </summary>
    [Column("mdc_description")]
    [StringLength(200)]
    public string? MDCDescription { get; set; }

    /// <summary>
    /// DRG type (MED = Medical, SURG = Surgical)
    /// </summary>
    [Column("drg_type")]
    [StringLength(10)]
    public string? DrgType { get; set; }

    /// <summary>
    /// Relative weight (for payment calculation)
    /// Higher weight = more complex/expensive
    /// </summary>
    [Column("relative_weight")]
    public decimal RelativeWeight { get; set; }

    /// <summary>
    /// Geometric mean length of stay (days)
    /// </summary>
    [Column("geometric_mean_los")]
    public decimal? GeometricMeanLOS { get; set; }

    /// <summary>
    /// Arithmetic mean length of stay (days)
    /// </summary>
    [Column("arithmetic_mean_los")]
    public decimal? ArithmeticMeanLOS { get; set; }

    /// <summary>
    /// Fiscal year effective
    /// </summary>
    [Column("fiscal_year")]
    public int FiscalYear { get; set; }

    /// <summary>
    /// Status (A = Active, D = Deleted)
    /// </summary>
    [Column("status")]
    [StringLength(1)]
    public string Status { get; set; } = "A";
}

/// <summary>
/// Place of Service codes (2-digit codes indicating where service was performed)
/// Examples: 11 = Office, 21 = Inpatient Hospital, 22 = Outpatient Hospital, 23 = Emergency Room
/// </summary>
[Table("place_of_service")]
public class PlaceOfService
{
    /// <summary>
    /// POS code (2 digits)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(2)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Description
    /// </summary>
    [Column("description")]
    [StringLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category (Facility, Non-Facility, Other)
    /// </summary>
    [Column("category")]
    [StringLength(50)]
    public string? Category { get; set; }

    /// <summary>
    /// Status (A = Active, D = Deleted)
    /// </summary>
    [Column("status")]
    [StringLength(1)]
    public string Status { get; set; } = "A";
}

/// <summary>
/// Revenue codes (4-digit codes for institutional billing)
/// Examples: 0450 = Emergency room, 0250 = Pharmacy, 0360 = Operating room
/// </summary>
[Table("revenue_codes")]
public class RevenueCode
{
    /// <summary>
    /// Revenue code (4 digits)
    /// </summary>
    [Key]
    [Column("code")]
    [StringLength(4)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Description
    /// </summary>
    [Column("description")]
    [StringLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category (Room & Board, Ancillary, etc.)
    /// </summary>
    [Column("category")]
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>
    /// Status (A = Active, D = Deleted)
    /// </summary>
    [Column("status")]
    [StringLength(1)]
    public string Status { get; set; } = "A";
}
