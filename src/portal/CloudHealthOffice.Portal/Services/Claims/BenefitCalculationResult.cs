namespace CloudHealthOffice.Portal.Services;

public class BenefitCalculationResult
{
    public string ServiceType { get; set; } = string.Empty;
    public string BenefitRuleApplied { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleApplied { get; set; }
    public decimal DeductibleRemaining { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal PlanPayment { get; set; }
    public decimal MemberResponsibility { get; set; }
    public bool DeductibleMet { get; set; }
    public bool OopMaxMet { get; set; }
    public decimal IndividualDeductibleBalance { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal IndividualOopBalance { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public List<AccumulatorUpdate> AccumulatorUpdates { get; set; } = new();
}
