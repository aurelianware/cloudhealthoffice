namespace CloudHealthOffice.Portal.Services;

public class NetworkTierBenefit
{
    public string TierName { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }
}
