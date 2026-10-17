namespace CloudHealthOffice.Portal.Services;

public class MemberAccumulators
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }
    public List<ServiceAccumulator> ServiceAccumulators { get; set; } = new();
    public List<AccumulatorActivity> RecentActivity { get; set; } = new();
}
