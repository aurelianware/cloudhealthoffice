namespace CloudHealthOffice.Portal.Services;

public class ArBatchRuleTestResult
{
    public string RuleCode { get; set; } = string.Empty;
    public decimal SampleAmount { get; set; }
    public string DebitAccountId { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public string CreditAccountId { get; set; } = string.Empty;
    public decimal CreditAmount { get; set; }
    public decimal? SponsorSplitAmount { get; set; }
    public decimal? MemberSplitAmount { get; set; }
}
