namespace CloudHealthOffice.Portal.Services;

public class ServiceAccumulator
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal Used { get; set; }
    public decimal Limit { get; set; }
    public string Unit { get; set; } = "USD";
}
