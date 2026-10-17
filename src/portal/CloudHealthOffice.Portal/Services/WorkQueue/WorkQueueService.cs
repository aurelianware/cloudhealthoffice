using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Work Queue Service
// ---------------------------------------------------------------------------

public class WorkQueueService : IWorkQueueService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkQueueService> _logger;

    public WorkQueueService(HttpClient httpClient, IConfiguration configuration, ILogger<WorkQueueService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WorkQueueSummary> GetQueueSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<WorkQueueSummary>($"{baseUrl}/Claims/work-queue/summary");
            return summary ?? new WorkQueueSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<WorkQueueItem>> GetQueueItemsAsync(string? queueType = null,
        string? assignedTo = null, int limit = 100)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/Claims/work-queue/items?limit={limit}";
            if (!string.IsNullOrEmpty(queueType)) url += $"&queueType={Uri.EscapeDataString(queueType)}";
            if (!string.IsNullOrEmpty(assignedTo)) url += $"&assignedTo={Uri.EscapeDataString(assignedTo)}";
            var items = await _httpClient.GetFromJsonAsync<List<WorkQueueItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task AssignClaimAsync(string claimId, string assignTo)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/assign",
                new { AssignTo = assignTo });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimResolutionResult> OverrideAsync(string claimId, string overrideReason, int? payerSequence = null,
        string? pendFingerprint = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/override",
                new { OverrideReason = overrideReason, PayerSequence = payerSequence, PendFingerprint = pendFingerprint });
            return await ReadResolutionAsync(response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimResolutionResult> ResolvePendedClaimAsync(
        string claimId,
        string disposition,
        string reason,
        string? aiExaminerAgreement,
        string examinerUserId,
        int? payerSequence = null,
        string? pendFingerprint = null,
        IReadOnlyList<EditDecisionInput>? editDecisions = null,
        IReadOnlyList<LineDenialInput>? lineDenials = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            // editDecisions present (even empty) makes the approval edit-level:
            // every edit not overridden is upheld. Absent keeps it whole-claim.
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/resolve",
                new
                {
                    disposition,
                    reason,
                    aiExaminerAgreement,
                    examinerUserId,
                    payerSequence,
                    pendFingerprint,
                    editDecisions,
                    lineDenials,
                });
            return await ReadResolutionAsync(response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    /// <summary>
    /// 202 → waiting for a second approver; 400 / 403 / 409 → the service's
    /// refusal and reasons (shown to the examiner, never "service
    /// unavailable"); anything else unsuccessful → service unavailable.
    /// </summary>
    private static async Task<ClaimResolutionResult> ReadResolutionAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            string? message = null;
            try
            {
                var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                if (body.ValueKind == System.Text.Json.JsonValueKind.Object
                    && body.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                    message = m.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
            }
            return new ClaimResolutionResult(true, message ?? "Waiting for a second approver.");
        }

        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.Conflict)
        {
            throw ClaimResolutionRefusedException.From(response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        response.EnsureSuccessStatusCode();
        return ClaimResolutionResult.Resolved;
    }
}
