namespace CloudHealthOffice.Portal.Services;

public class CapitationPeriodSummaryDto
{
    public DateTime Period { get; set; }
    public int TotalProviders { get; set; }
    public int TotalMemberMonths { get; set; }
    public decimal TotalGrossCapitation { get; set; }
    public decimal TotalWithholds { get; set; }
    public decimal TotalNetPayable { get; set; }
    public Dictionary<string, decimal> ByLineOfBusiness { get; set; } = new();
    public Dictionary<string, decimal> ByContractType { get; set; } = new();
}
