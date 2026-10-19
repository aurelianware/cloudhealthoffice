namespace CloudHealthOffice.Portal.Services;

// ── PR17: Workflow Extensions ────────────────────────────────────────────────

public class WorkflowRunExtended : WorkflowRun
{
    public string WorkflowTemplate { get; set; } = string.Empty;
    public string TriggerSource { get; set; } = string.Empty; // API, Scheduled, Manual
    public int StepCount { get; set; }
    public int CompletedStepCount { get; set; }
    public List<WorkflowStepExtended> DetailedSteps { get; set; } = new();
}
