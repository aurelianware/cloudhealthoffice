namespace CloudHealthOffice.Portal.Services;

public class CapitationRateConfigSummary
{
    public string Id { get; set; } = string.Empty;
    public string RateConfigNumber { get; set; } = string.Empty;
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "Individual";
    public string ContractType { get; set; } = "PrimaryCareOnly";
    public string LineOfBusiness { get; set; } = "Commercial";
    public DateTime? LastDenormSyncAt { get; set; }
    public List<string> PlanIds { get; set; } = new();
    public List<CapRateTier> RateTiers { get; set; } = new();
    public bool RiskAdjusted { get; set; }
    public decimal DefaultRiskScore { get; set; } = 1.0m;
    public decimal WithholdPercentage { get; set; }
    public decimal? IncentivePoolPercentage { get; set; }
    public decimal? StopLossThreshold { get; set; }
    public decimal? AggregateStopLoss { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = "Draft";
}
