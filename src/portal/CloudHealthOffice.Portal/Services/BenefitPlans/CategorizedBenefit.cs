namespace CloudHealthOffice.Portal.Services;

public class CategorizedBenefit
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string? Description { get; set; }
    public NetworkTierBenefit InNetwork { get; set; } = new();
    public NetworkTierBenefit? OutOfNetwork { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool OopApplies { get; set; }
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public decimal? AnnualMaximum { get; set; }
    public decimal? LifetimeMaximum { get; set; }
    public string? Limitations { get; set; }
    public PharmacyDetail? Pharmacy { get; set; }
}
