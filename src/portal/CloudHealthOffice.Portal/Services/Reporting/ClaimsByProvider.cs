namespace CloudHealthOffice.Portal.Services;

public class ClaimsByProvider
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public double DenialRate { get; set; }
    public double AvgProcessingDays { get; set; }
}
