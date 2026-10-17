namespace CloudHealthOffice.Portal.Services;

// ── PR16: Enhanced Benefit Configuration ────────────────────────────────────

public class ServiceBenefitRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty; // Medical, Pharmacy, Dental, Vision, MentalHealth
    public string ServiceTypeCode { get; set; } = string.Empty;
    public string ServiceTypeDescription { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty; // Tier1, Tier2, OutOfNetwork
    public decimal? Copay { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public bool SubjectToDeductible { get; set; }
    public int? AnnualVisitLimit { get; set; }
    public decimal? AnnualDollarLimit { get; set; }
    public bool PriorAuthRequired { get; set; }
    public string? PriorAuthThreshold { get; set; }
    public string DeductibleAccumulatorGroup { get; set; } = "Individual";
    public string OopAccumulatorGroup { get; set; } = "Individual";
    public bool CrossAccumulatesWithMedical { get; set; }
    public bool IsEditing { get; set; } // UI state
}
