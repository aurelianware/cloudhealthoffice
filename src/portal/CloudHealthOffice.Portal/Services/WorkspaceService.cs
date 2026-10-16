using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>A plan notice (work-management-service <c>/workspace/notices</c>).</summary>
public sealed class PlanNoticeView
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Info or Important.</summary>
    public string Severity { get; set; } = "Info";

    public DateTime StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> TargetRoles { get; set; } = new();

    /// <summary>Scheduled, Live, Expired or Withdrawn.</summary>
    public string State { get; set; } = "Live";

    public bool Read { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public bool Important => string.Equals(Severity, "Important", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What an administrator posts or edits.</summary>
public sealed class PlanNoticeInput
{
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string Severity { get; set; } = "Info";
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> TargetRoles { get; set; } = new();
}

/// <summary>A record the user pinned to Home.</summary>
public sealed class PinnedRecordView
{
    public string Kind { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTime PinnedAt { get; set; }

    public string Href => Kind switch
    {
        "Member" => $"/members/{Uri.EscapeDataString(RecordId)}",
        "Claim" => $"/claims/{Uri.EscapeDataString(RecordId)}",
        "Provider" => $"/providers/{Uri.EscapeDataString(RecordId)}",
        "Authorization" => $"/authorizations/{Uri.EscapeDataString(RecordId)}",
        "Appeal" => $"/appeals/{Uri.EscapeDataString(RecordId)}",
        _ => "/home",
    };
}

/// <summary>Plan notices and pinned records (Home).</summary>
public interface IWorkspaceService
{
    Task<List<PlanNoticeView>> GetMyNoticesAsync();
    Task MarkNoticeReadAsync(string id);
    Task<List<PlanNoticeView>> GetAllNoticesAsync();
    Task<PlanNoticeView> PostNoticeAsync(PlanNoticeInput input);
    Task<PlanNoticeView> EditNoticeAsync(string id, PlanNoticeInput input);
    Task WithdrawNoticeAsync(string id);
    Task<List<PinnedRecordView>> GetPinsAsync();
    Task PinAsync(string kind, string recordId, string label);
    Task UnpinAsync(string kind, string recordId);
}

/// <summary>HTTP client for work-management-service <c>/workspace</c>.</summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(HttpClient http, IConfiguration configuration, ILogger<WorkspaceService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["Services:WorkManagementService"] ?? "http://work-management-service.cloudhealthoffice/api/v1").TrimEnd('/') + "/workspace";

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
                var text = await response.Content.ReadAsStringAsync();
                string message = text;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("error", out var error)) message = error.GetString() ?? text;
                }
                catch (JsonException) { }
                throw new WorkManagementRefusedException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "Refused" : message);
            }
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", WorkManagementService.ServiceName);
            throw new ServiceUnavailableException(WorkManagementService.ServiceName, ex);
        }
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : new() =>
        await response.Content.ReadFromJsonAsync<T>(Json) ?? new T();

    public async Task<List<PlanNoticeView>> GetMyNoticesAsync() =>
        await ReadAsync<List<PlanNoticeView>>(await SendAsync(HttpMethod.Get, "notices"));

    public async Task MarkNoticeReadAsync(string id) =>
        (await SendAsync(HttpMethod.Post, $"notices/{E(id)}/read")).Dispose();

    public async Task<List<PlanNoticeView>> GetAllNoticesAsync() =>
        await ReadAsync<List<PlanNoticeView>>(await SendAsync(HttpMethod.Get, "notices/all"));

    public async Task<PlanNoticeView> PostNoticeAsync(PlanNoticeInput input) =>
        await ReadAsync<PlanNoticeView>(await SendAsync(HttpMethod.Post, "notices", input));

    public async Task<PlanNoticeView> EditNoticeAsync(string id, PlanNoticeInput input) =>
        await ReadAsync<PlanNoticeView>(await SendAsync(HttpMethod.Put, $"notices/{E(id)}", input));

    public async Task WithdrawNoticeAsync(string id) =>
        (await SendAsync(HttpMethod.Post, $"notices/{E(id)}/withdraw")).Dispose();

    public async Task<List<PinnedRecordView>> GetPinsAsync() =>
        await ReadAsync<List<PinnedRecordView>>(await SendAsync(HttpMethod.Get, "pins"));

    public async Task PinAsync(string kind, string recordId, string label) =>
        (await SendAsync(HttpMethod.Put, "pins", new { kind, recordId, label })).Dispose();

    public async Task UnpinAsync(string kind, string recordId) =>
        (await SendAsync(HttpMethod.Delete, $"pins/{E(kind)}/{E(recordId)}")).Dispose();
}
