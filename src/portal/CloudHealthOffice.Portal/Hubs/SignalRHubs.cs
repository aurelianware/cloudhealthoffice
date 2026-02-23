using Microsoft.AspNetCore.SignalR;

namespace CloudHealthOffice.Portal.Hubs;

public class ClaimsHub : Hub
{
    public async Task SendClaimStatusUpdate(string claimId, string status, int processingTimeMs)
    {
        await Clients.All.SendAsync("ReceiveClaimStatus", claimId, status, processingTimeMs);
    }

    public async Task SendClaimApproved(string claimId, decimal payerAmount, decimal patientResponsibility)
    {
        await Clients.All.SendAsync("ClaimApproved", claimId, payerAmount, patientResponsibility);
    }

    public async Task SendClaimDenied(string claimId, string reason, string carcCode)
    {
        await Clients.All.SendAsync("ClaimDenied", claimId, reason, carcCode);
    }
}

public class WorkflowHub : Hub
{
    public async Task SendWorkflowUpdate(string workflowId, string status, string currentStep)
    {
        await Clients.All.SendAsync("ReceiveWorkflowUpdate", workflowId, status, currentStep);
    }

    public async Task SendWorkflowCompleted(string workflowId, int durationMs)
    {
        await Clients.All.SendAsync("WorkflowCompleted", workflowId, durationMs);
    }
}
