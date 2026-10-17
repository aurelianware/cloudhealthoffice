namespace CloudHealthOffice.Portal.Services;

public class EligibilityStatsReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalRequests { get; set; }
    public int EligibleCount { get; set; }
    public int IneligibleCount { get; set; }
    public double EligibilityRate { get; set; }
    public double AvgResponseTimeMs { get; set; }
}
