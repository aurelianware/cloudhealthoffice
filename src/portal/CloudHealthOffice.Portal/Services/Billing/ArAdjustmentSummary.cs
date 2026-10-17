namespace CloudHealthOffice.Portal.Services;

public class ArAdjustmentSummary
{
    public string Id { get; set; } = string.Empty;
    public string AdjustmentNumber { get; set; } = string.Empty;
    public string AdjustmentType { get; set; } = "ManualCorrection";
    public string GlAccountId { get; set; } = string.Empty;
    public string ArBalanceId { get; set; } = string.Empty;
    public DateTime Period { get; set; }
    public decimal Amount { get; set; }
    public string Direction { get; set; } = "Debit";
    public string ReasonCode { get; set; } = string.Empty;
    public string? Narrative { get; set; }
    public string? AuthorizedBy { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public string? SourceType { get; set; }
    public string? SourceReferenceId { get; set; }
    public string Status { get; set; } = "Pending";
}
