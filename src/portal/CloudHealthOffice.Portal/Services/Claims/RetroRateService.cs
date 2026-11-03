using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Retro rate reprocessing: fee schedule changes (benefit-plan-service
/// <c>/api/v1/fee-schedules/changes</c>), dry-run impact runs and the
/// reprocessing runs that turn them into adjustments (claims-service
/// <c>/api/v1/claims/retro-rate</c>). See docs/integration/RETRO-RATE-REPROCESSING.md.
/// </summary>
public interface IRetroRateService
{
    Task<List<FeeScheduleChangeView>> ListChangesAsync(int limit = 50);
    Task<List<RetroRateImpactRunView>> ListImpactRunsAsync(int limit = 50);

    /// <summary>Measures a change. A refusal (no such change, no permission) comes back in <see cref="RetroRateResult{T}.Error"/>.</summary>
    Task<RetroRateResult<RetroRateImpactRunView>> RunImpactAsync(string feeScheduleId, long? revision);
    Task<List<RetroRateImpactResultView>> ListImpactResultsAsync(string runId, string? direction, int limit = 200);

    Task<List<RetroRateReprocessingRunView>> ListReprocessingRunsAsync(int limit = 50);
    Task<RetroRateResult<RetroRateReprocessingRunView>> ProposeAsync(string impactRunId);
    Task<RetroRateResult<RetroRateReprocessingRunView>> ApproveAsync(string runId);
    Task<RetroRateResult<RetroRateReprocessingRunView>> ExecuteAsync(string runId);
    Task<RetroRateResult<RetroRateReprocessingRunView>> CancelAsync(string runId);
}

public sealed class RetroRateService : IRetroRateService
{
    private const string ClaimsServiceName = "Claims Service";
    private const string BenefitPlanServiceName = "Benefit Plan Service";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetroRateService> _logger;

    public RetroRateService(HttpClient http, IConfiguration configuration, ILogger<RetroRateService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string RetroRate => $"{_configuration["Services:ClaimsService"]}/v1/claims/retro-rate";
    private string Changes => $"{_configuration["Services:BenefitPlanService"]}/v1/fee-schedules/changes";

    public Task<List<FeeScheduleChangeView>> ListChangesAsync(int limit = 50) => Call(BenefitPlanServiceName, async () =>
        await _http.GetFromJsonAsync<List<FeeScheduleChangeView>>($"{Changes}?limit={Math.Clamp(limit, 1, 200)}", Json) ?? new());

    public Task<List<RetroRateImpactRunView>> ListImpactRunsAsync(int limit = 50) => Call(ClaimsServiceName, async () =>
        await _http.GetFromJsonAsync<List<RetroRateImpactRunView>>($"{RetroRate}/impact-runs?limit={Math.Clamp(limit, 1, 200)}", Json) ?? new());

    public Task<RetroRateResult<RetroRateImpactRunView>> RunImpactAsync(string feeScheduleId, long? revision) =>
        Send<RetroRateImpactRunView>(HttpMethod.Post, $"{RetroRate}/impact-runs", new { feeScheduleId, revision });

    public Task<List<RetroRateImpactResultView>> ListImpactResultsAsync(string runId, string? direction, int limit = 200) => Call(ClaimsServiceName, async () =>
    {
        var url = $"{RetroRate}/impact-runs/{Uri.EscapeDataString(runId)}/results?limit={Math.Clamp(limit, 1, 1000)}"
                  + (string.IsNullOrWhiteSpace(direction) ? "" : $"&direction={Uri.EscapeDataString(direction)}");
        using var response = await _http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound) return new List<RetroRateImpactResultView>();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RetroRateImpactResultView>>(Json) ?? new();
    });

    public Task<List<RetroRateReprocessingRunView>> ListReprocessingRunsAsync(int limit = 50) => Call(ClaimsServiceName, async () =>
        await _http.GetFromJsonAsync<List<RetroRateReprocessingRunView>>($"{RetroRate}/reprocessing-runs?limit={Math.Clamp(limit, 1, 200)}", Json) ?? new());

    public Task<RetroRateResult<RetroRateReprocessingRunView>> ProposeAsync(string impactRunId) =>
        Send<RetroRateReprocessingRunView>(HttpMethod.Post, $"{RetroRate}/reprocessing-runs", new { impactRunId });

    public Task<RetroRateResult<RetroRateReprocessingRunView>> ApproveAsync(string runId) => Transition(runId, "approve");
    public Task<RetroRateResult<RetroRateReprocessingRunView>> ExecuteAsync(string runId) => Transition(runId, "execute");
    public Task<RetroRateResult<RetroRateReprocessingRunView>> CancelAsync(string runId) => Transition(runId, "cancel");

    private Task<RetroRateResult<RetroRateReprocessingRunView>> Transition(string runId, string action) =>
        Send<RetroRateReprocessingRunView>(HttpMethod.Post, $"{RetroRate}/reprocessing-runs/{Uri.EscapeDataString(runId)}/{action}", body: null);

    private Task<RetroRateResult<T>> Send<T>(HttpMethod method, string url, object? body) where T : class => Call(ClaimsServiceName, async () =>
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return new RetroRateResult<T> { Value = JsonSerializer.Deserialize<T>(text, Json) };
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return new RetroRateResult<T> { Error = "You need the claims:adjust permission for retro rate reprocessing." };
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return new RetroRateResult<T> { Error = ReadError(text) ?? $"The request was refused ({(int)response.StatusCode})." };
        response.EnsureSuccessStatusCode();
        return new RetroRateResult<T> { Error = "Unexpected response." };
    });

    /// <summary>claims-service answers refusals as <c>{ "error": "..." }</c>.</summary>
    public static string? ReadError(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject o
                ? (o["error"] ?? o["message"] ?? o["title"])?.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<T> Call<T>(string serviceName, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", serviceName);
            throw new ServiceUnavailableException(serviceName, ex);
        }
    }
}

public sealed class RetroRateResult<T> where T : class
{
    public T? Value { get; set; }
    public string? Error { get; set; }
}

public sealed class FeeScheduleChangeView
{
    public string Id { get; set; } = string.Empty;
    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    /// <summary>benefit-plan-service writes the enum as a number (0 Created, 1 Replaced) or, if configured, its name.</summary>
    public JsonElement Kind { get; set; }
    public long? PreviousRevision { get; set; }
    public long NewRevision { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public string? DefaultForPlanId { get; set; }
    public bool AllCodes { get; set; }
    public List<string> ScheduleLevelChanges { get; set; } = new();
    public List<string> ChangedCodes { get; set; } = new();
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int LinesRateIncreased { get; set; }
    public int LinesRateDecreased { get; set; }
    public DateTime ChangedAt { get; set; }

    public string KindLabel => Kind.ValueKind switch
    {
        JsonValueKind.Number when Kind.TryGetInt32(out var n) => n == 0 ? "Created" : n == 1 ? "Replaced" : n.ToString(),
        JsonValueKind.String => Kind.GetString() ?? string.Empty,
        _ => string.Empty,
    };

    public string CodesSummary => AllCodes
        ? "All codes"
        : ChangedCodes.Count == 0 ? "No rate changes" : $"{ChangedCodes.Count:N0} code{(ChangedCodes.Count == 1 ? "" : "s")}";
}

public sealed class RetroRateImpactRunView
{
    public string Id { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = string.Empty;
    public long NewRevision { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public int CandidatesRead { get; set; }
    public bool Truncated { get; set; }
    public int ClaimsAffected { get; set; }
    public Dictionary<string, int> CountsByDirection { get; set; } = new();
    public Dictionary<string, int> CountsByMatch { get; set; } = new();
    public decimal TotalUnderpaid { get; set; }
    public decimal TotalOverpaid { get; set; }
    public decimal NetPaidDelta { get; set; }
    public decimal NetMemberLiabilityDelta { get; set; }
    public List<RetroRateProviderTotalView> TopProviders { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    public int Count(string direction) => CountsByDirection.TryGetValue(direction, out var n) ? n : 0;
}

public sealed class RetroRateProviderTotalView
{
    public string BillingProviderNpi { get; set; } = string.Empty;
    public int Claims { get; set; }
    public decimal Underpaid { get; set; }
    public decimal Overpaid { get; set; }
    public decimal Net { get; set; }
}

public sealed class RetroRateImpactResultView
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string ClaimStatus { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public string? BillingProviderNpi { get; set; }
    public string? BenefitPlanId { get; set; }
    public string Match { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public List<int> AffectedLines { get; set; } = new();
    public long? PricedOnRevision { get; set; }
    public decimal Paid { get; set; }
    public decimal Repriced { get; set; }
    public decimal NetPaidDelta { get; set; }
    public decimal MemberLiabilityDelta { get; set; }
    public string? PendCode { get; set; }
    public string? PendReason { get; set; }
}

public sealed class RetroRateReprocessingRunView
{
    public string Id { get; set; } = string.Empty;
    public string ImpactRunId { get; set; } = string.Empty;
    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ExecutedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CancelledBy { get; set; }
    public int ClaimsToAdjust { get; set; }
    public decimal PlannedAdditionalPayment { get; set; }
    public decimal PlannedRecoupment { get; set; }
    public Dictionary<string, int> SkippedByAction { get; set; } = new();
    public Dictionary<string, int> CountsByOutcome { get; set; } = new();
    public List<RetroRateReprocessingItemView> Items { get; set; } = new();
}

public sealed class RetroRateReprocessingItemView
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? BillingProviderNpi { get; set; }
    public string? BenefitPlanId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal NetPaidDelta { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? AdjustmentId { get; set; }
    public string? Message { get; set; }
}
