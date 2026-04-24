namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a synthetic benefit plan configuration with cost-sharing rules.
/// Structurally compatible with the production BenefitPlan entity.
/// </summary>
public class SyntheticBenefitPlan
{
    /// <summary>Unique plan identifier (e.g., PLN-STAR-ADULT-001).</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Human-readable plan name.</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>Payer name (e.g., Texas Medicaid MCO).</summary>
    public string Payer { get; set; } = "Texas Medicaid MCO";

    /// <summary>Tenant identifier.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Plan type: HMO, PPO, Medicaid, etc.</summary>
    public string PlanType { get; set; } = "Medicaid";

    /// <summary>Line of business: Medicaid, Commercial, Medicare, etc.</summary>
    public string LineOfBusiness { get; set; } = "Medicaid";

    /// <summary>Medicaid sub-program: STAR, CHIP, STAR+PLUS, STAR Kids, STAR Health.</summary>
    public string? MedicaidProgram { get; set; }

    /// <summary>Plan effective date.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Plan termination date (null if active).</summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>Whether the plan is currently active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Individual in-network deductible amount.</summary>
    public decimal IndividualDeductible { get; set; }

    /// <summary>Family in-network deductible amount.</summary>
    public decimal FamilyDeductible { get; set; }

    /// <summary>Individual in-network out-of-pocket maximum.</summary>
    public decimal IndividualOopMax { get; set; }

    /// <summary>Family in-network out-of-pocket maximum.</summary>
    public decimal FamilyOopMax { get; set; }

    /// <summary>PCP copay amount.</summary>
    public decimal PcpCopay { get; set; }

    /// <summary>Specialist copay amount.</summary>
    public decimal SpecialistCopay { get; set; }

    /// <summary>Emergency room copay amount.</summary>
    public decimal ErCopay { get; set; }

    /// <summary>Inpatient copay/coinsurance description.</summary>
    public decimal InpatientCopay { get; set; }

    /// <summary>Inpatient per-diem copay (e.g., $100/day for CHIP Plan B).</summary>
    public decimal InpatientPerDiem { get; set; }

    /// <summary>Default coinsurance percentage (e.g., 0.20 = 20%).</summary>
    public decimal CoinsurancePercent { get; set; }

    /// <summary>Out-of-network individual deductible.</summary>
    public decimal? OutOfNetworkDeductible { get; set; }

    /// <summary>Out-of-network individual OOP max.</summary>
    public decimal? OutOfNetworkOopMax { get; set; }

    /// <summary>Whether PCP referral is required for specialist visits (HMO gatekeeper).</summary>
    public bool RequiresPcpReferral { get; set; }

    /// <summary>Dental annual maximum (for dental plans).</summary>
    public decimal? DentalAnnualMax { get; set; }

    /// <summary>Vision annual maximum (for vision plans).</summary>
    public decimal? VisionAnnualMax { get; set; }

    /// <summary>Benefit detail entries by service category.</summary>
    public List<SyntheticBenefit> Benefits { get; set; } = new();
}

/// <summary>
/// Represents a specific benefit within a plan for a service category.
/// </summary>
public class SyntheticBenefit
{
    /// <summary>Service category (e.g., "Office Visit", "Inpatient", "Emergency").</summary>
    public string ServiceCategory { get; set; } = string.Empty;

    /// <summary>Description of the benefit.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>In-network copay amount.</summary>
    public decimal? InNetworkCopay { get; set; }

    /// <summary>Out-of-network copay amount.</summary>
    public decimal? OutNetworkCopay { get; set; }

    /// <summary>In-network coinsurance percentage.</summary>
    public decimal? InNetworkCoinsurance { get; set; }

    /// <summary>Out-of-network coinsurance percentage.</summary>
    public decimal? OutNetworkCoinsurance { get; set; }

    /// <summary>Whether deductible applies to this benefit.</summary>
    public bool DeductibleApplies { get; set; }

    /// <summary>Whether prior authorization is required.</summary>
    public bool PriorAuthRequired { get; set; }

    /// <summary>Associated CPT/HCPCS codes.</summary>
    public List<string> CptCodes { get; set; } = new();
}
