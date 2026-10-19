using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class WorkflowService : IWorkflowService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public WorkflowService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?limit={limit}");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            return await _httpClient.GetFromJsonAsync<WorkflowDetails>($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<List<WorkflowRun>> GetActiveWorkflowsAsync()
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?phase=Running");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<bool> RetriggerWorkflowAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }
}
