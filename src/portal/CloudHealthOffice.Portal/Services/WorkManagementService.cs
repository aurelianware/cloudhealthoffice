using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// HTTP client for work-management-service. Uses the portal's scoped
/// <see cref="HttpClient"/> (ChoBearerTokenHandler adds the signed-in user's
/// CHO token and X-Tenant-ID for configured CHO hosts).
/// </summary>
public sealed class WorkManagementService : IWorkManagementService
{
    public const string ServiceName = "Work Management Service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkManagementService> _logger;

    public WorkManagementService(HttpClient http, IConfiguration configuration, ILogger<WorkManagementService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["Services:WorkManagementService"] ?? "http://work-management-service.cloudhealthoffice/api/v1").TrimEnd('/');

    private static string E(string value) => Uri.EscapeDataString(value);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            var response = await _http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                throw new WorkManagementRefusedException(response.StatusCode, await ErrorOf(response));
            }
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch (JsonException)
        {
            // Not a JSON error body: the status-based message (or the raw text) below is used instead.
        }
        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You do not have permission for this work-management action.",
            HttpStatusCode.NotFound => "The work item or setting was not found.",
            _ => string.IsNullOrWhiteSpace(text) ? $"Request refused ({(int)response.StatusCode})." : text,
        };
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, object? body = null) where T : new()
    {
        using var response = await SendAsync(method, path, body);
        if (response.StatusCode == HttpStatusCode.NoContent) return new T();
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? new T();
    }

    // ── My Work ──────────────────────────────────────────────────────────

    public async Task<WorkNextResult?> GetNextAsync(string? workType = null)
    {
        var path = "work/next" + (string.IsNullOrWhiteSpace(workType) ? string.Empty : $"?workType={E(workType)}");
        using var response = await SendAsync(HttpMethod.Post, path);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<WorkNextResult>(Json);
    }

    public Task<List<WorkItemView>> GetMineAsync() => ReadAsync<List<WorkItemView>>(HttpMethod.Get, "work/mine");

    public async Task<WorkItemView> GetItemAsync(string workItemId) =>
        (await ReadAsync<WorkItemDetailResponse>(HttpMethod.Get, $"work/{E(workItemId)}")).Item;
    public Task<WorkItemView> CompleteAsync(string workItemId, string resolution) =>
        ReadAsync<WorkItemView>(HttpMethod.Post, $"work/{E(workItemId)}/complete", new { resolution });
    public sealed class WorkItemDetailResponse { public WorkItemView Item { get; set; } = new(); }

    public Task<WorkItemView> ReleaseAsync(string workItemId) => ReadAsync<WorkItemView>(HttpMethod.Post, $"work/{E(workItemId)}/release");

    public Task<WorkItemView> RenewAsync(string workItemId) => ReadAsync<WorkItemView>(HttpMethod.Post, $"work/{E(workItemId)}/renew");

    // ── Supervisor ───────────────────────────────────────────────────────

    public Task<List<WorkQueueCounts>> GetQueuesAsync() => ReadAsync<List<WorkQueueCounts>>(HttpMethod.Get, "work/queues");

    public Task<WorkItemPage> ListAsync(string? queueId = null, string? status = null, string? assignedTo = null, int page = 1, int pageSize = 200)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(queueId)) query.Add($"queueId={E(queueId)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={E(status)}");
        if (!string.IsNullOrWhiteSpace(assignedTo)) query.Add($"assignedTo={E(assignedTo)}");
        return ReadAsync<WorkItemPage>(HttpMethod.Get, "work?" + string.Join('&', query));
    }

    public Task<WorkItemView> AssignAsync(string workItemId, string userId) =>
        ReadAsync<WorkItemView>(HttpMethod.Post, $"work/{E(workItemId)}/assign", new { userId });

    public Task<WorkItemView> UnassignAsync(string workItemId) =>
        ReadAsync<WorkItemView>(HttpMethod.Post, $"work/{E(workItemId)}/unassign");

    public Task<List<string>> GetAssigneesAsync() => ReadAsync<List<string>>(HttpMethod.Get, "work/assignees");

    // ── Admin ────────────────────────────────────────────────────────────

    public Task<List<WorkQueueConfig>> GetQueueConfigsAsync() => ReadAsync<List<WorkQueueConfig>>(HttpMethod.Get, "work/admin/queues");

    public Task<WorkQueueConfig> SaveQueueAsync(string? queueId, WorkQueueInput input) => queueId is null
        ? ReadAsync<WorkQueueConfig>(HttpMethod.Post, "work/admin/queues", input)
        : ReadAsync<WorkQueueConfig>(HttpMethod.Put, $"work/admin/queues/{E(queueId)}", input);

    public async Task DeleteQueueAsync(string queueId) => (await SendAsync(HttpMethod.Delete, $"work/admin/queues/{E(queueId)}")).Dispose();

    public async Task ReorderQueuesAsync(string workType, IReadOnlyList<string> queueIds) =>
        (await SendAsync(HttpMethod.Post, "work/admin/queues/reorder", new { workType, queueIds })).Dispose();

    public Task<List<WorkTeamConfig>> GetTeamsAsync() => ReadAsync<List<WorkTeamConfig>>(HttpMethod.Get, "work/admin/teams");

    public Task<WorkTeamConfig> SaveTeamAsync(string? teamId, string name, IReadOnlyList<string> userIds) => teamId is null
        ? ReadAsync<WorkTeamConfig>(HttpMethod.Post, "work/admin/teams", new { name, userIds })
        : ReadAsync<WorkTeamConfig>(HttpMethod.Put, $"work/admin/teams/{E(teamId)}", new { name, userIds });

    public async Task DeleteTeamAsync(string teamId) => (await SendAsync(HttpMethod.Delete, $"work/admin/teams/{E(teamId)}")).Dispose();

    public Task<List<WorkMembershipConfig>> GetMembershipsAsync() => ReadAsync<List<WorkMembershipConfig>>(HttpMethod.Get, "work/admin/memberships");

    public Task<WorkMembershipConfig> SaveMembershipAsync(string? membershipId, WorkMembershipInput input) => membershipId is null
        ? ReadAsync<WorkMembershipConfig>(HttpMethod.Post, "work/admin/memberships", input)
        : ReadAsync<WorkMembershipConfig>(HttpMethod.Put, $"work/admin/memberships/{E(membershipId)}", input);

    public async Task DeleteMembershipAsync(string membershipId) =>
        (await SendAsync(HttpMethod.Delete, $"work/admin/memberships/{E(membershipId)}")).Dispose();

    public Task<List<EffectiveWorkQueue>> GetEffectiveQueuesAsync(string userId) =>
        ReadAsync<List<EffectiveWorkQueue>>(HttpMethod.Get, $"work/admin/users/{E(userId)}/queues");

    public Task<WorkRecomputeResult> RecomputeAsync() => ReadAsync<WorkRecomputeResult>(HttpMethod.Post, "work/admin/recompute");

    public async Task<WorkReconcileResult> ReconcileAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/work/admin/reconcile");
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new WorkManagementRefusedException(response.StatusCode, await ErrorOf(response));
            // 502: the reconcile ran but claims-service was unreachable; the body says so.
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway)
                return await response.Content.ReadFromJsonAsync<WorkReconcileResult>(Json) ?? new WorkReconcileResult();
            response.EnsureSuccessStatusCode();
            return new WorkReconcileResult();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}
