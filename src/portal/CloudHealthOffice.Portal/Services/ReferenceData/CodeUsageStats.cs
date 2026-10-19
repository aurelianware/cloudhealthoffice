namespace CloudHealthOffice.Portal.Services;

public class CodeUsageStats
{
    public string CodeSystem { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int ClaimsCount { get; set; }
    public int AuthorizationsCount { get; set; }
    public int BenefitsCount { get; set; }
    public DateTime? LastUsedDate { get; set; }
    public decimal TotalBilledAmount { get; set; }
}
