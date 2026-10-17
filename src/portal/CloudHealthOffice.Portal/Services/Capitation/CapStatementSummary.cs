namespace CloudHealthOffice.Portal.Services;

public class CapStatementSummary
{
    public string Id { get; set; } = string.Empty;
    public string StatementNumber { get; set; } = string.Empty;
    public string? CapitationRunId { get; set; }
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime CapitationPeriodStart { get; set; }
    public DateTime CapitationPeriodEnd { get; set; }
    public string Status { get; set; } = "Generated";
    public int MemberMonths { get; set; }
    public decimal GrossCapitation { get; set; }
    public decimal WithholdAmount { get; set; }
    public decimal TotalAdjustments { get; set; }
    public decimal NetPayable { get; set; }
    public DateTime? PaymentDate { get; set; }
    public List<CapLineItem> LineItems { get; set; } = new();
    public List<CapAdjustment> Adjustments { get; set; } = new();
}
