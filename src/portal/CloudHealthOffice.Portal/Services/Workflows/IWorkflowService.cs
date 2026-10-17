namespace CloudHealthOffice.Portal.Services;

public interface IWorkflowService
{
    Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20);
    Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId);
    Task<List<WorkflowRun>> GetActiveWorkflowsAsync();
    Task<bool> RetriggerWorkflowAsync(string workflowId);
}
