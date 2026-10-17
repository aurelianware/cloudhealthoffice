namespace CloudHealthOffice.Portal.Services;

public class WorkflowDetails : WorkflowRun
{
    public List<WorkflowStep> Steps { get; set; } = new();
}
