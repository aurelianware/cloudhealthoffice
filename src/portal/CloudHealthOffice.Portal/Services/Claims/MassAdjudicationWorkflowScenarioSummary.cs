namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationWorkflowScenarioSummary
{
    public string Scenario { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Matches { get; set; }
    public int Mismatches { get; set; }
    public int Unsupported { get; set; }
    public int ObservationTimeouts { get; set; }
    public int Unspecified { get; set; }
}
