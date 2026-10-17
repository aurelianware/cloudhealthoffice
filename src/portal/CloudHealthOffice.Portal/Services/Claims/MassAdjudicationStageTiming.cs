namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationStageTiming
{
    public string Label { get; set; } = string.Empty;
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
}
