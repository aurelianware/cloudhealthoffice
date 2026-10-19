namespace CloudHealthOffice.Portal.Services;

// ── AR DTOs ─────────────────────────────────────────────────────────────────

public class GlAccountSummary
{
    public string Id { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string AccountType { get; set; } = "Asset";
    public string NormalBalance { get; set; } = "Debit";
    public string? SubType { get; set; }
    public string? StatementSection { get; set; }
    public string Segments { get; set; } = string.Empty;
    public GlSegmentCodesSummary SegmentCodes { get; set; } = new();
    public List<string> LineOfBusinessMapping { get; set; } = new();
    public PremiumSplitSummary? PremiumSplit { get; set; }
    public bool IsReconciliationAccount { get; set; }
    public string? ReconciliationPairAccountId { get; set; }
    public bool IsIntercompany { get; set; }
    public string? IntercompanyEntityCode { get; set; }
    public List<string> BatchRuleIds { get; set; } = new();
    public string Status { get; set; } = "Active";
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}
