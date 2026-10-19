namespace CloudHealthOffice.Portal.Services;

public class SponsorSummary
{
    public string SponsorId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Employer, Union, Association
    public string State { get; set; } = string.Empty;
    public int ActiveBenefitPlans { get; set; }
    public int TotalMembers { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Inactive, Pending
    public DateTime ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }
}
