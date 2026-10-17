namespace CloudHealthOffice.Portal.Services;

public class WorkflowStepExtended : WorkflowStep
{
    public int? DurationMs { get; set; }
    public string? NodeName { get; set; }
    public string? Message { get; set; }
    public int StepNumber { get; set; }
}
