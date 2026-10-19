namespace CloudHealthOffice.Portal.Services;

public class ArBalanceSummary
{
    public string Id { get; set; } = string.Empty;
    public string GlAccountId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public DateTime Period { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal SponsorBalance { get; set; }
    public decimal MemberBalance { get; set; }
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }
    public bool IsReconciled { get; set; }
    public DateTime? ReconciledAt { get; set; }
}
