namespace CloudHealthOffice.Portal.Services;

public class ArBatchRuleSummary
{
    public string Id { get; set; } = string.Empty;
    public string RuleCode { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public List<string> ApplicableLobs { get; set; } = new();
    public string DebitAccountId { get; set; } = string.Empty;
    public string CreditAccountId { get; set; } = string.Empty;
    public string SplitBehavior { get; set; } = "NoSplit";
    public decimal? AutoApproveThreshold { get; set; }
    public int ExecutionOrder { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}
