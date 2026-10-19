namespace CloudHealthOffice.Portal.Services;

public class ContractAmendmentSummary
{
    public string Id { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public string AmendmentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
