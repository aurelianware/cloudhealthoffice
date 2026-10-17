namespace CloudHealthOffice.Portal.Services;

public class UpdateServiceBenefitRulesRequest
{
    public string PlanId { get; set; } = string.Empty;
    public List<ServiceBenefitRule> Rules { get; set; } = new();
    public AccumulatorConfiguration Accumulators { get; set; } = new();
}
