using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The CMS-0057-F console's view of prior authorization: decision clocks for
/// open requests, recent decisions and the public-reporting metrics, all read
/// from authorization-service for the signed-in user's tenant.
/// </summary>
public interface ICms0057Service
{
    /// <summary>Every open request with its decision clock, most urgent first.</summary>
    Task<List<PriorAuthClock>> GetDecisionClocksAsync();

    Task<List<PriorAuthDecision>> GetRecentDecisionsAsync(int days = 30, int limit = 100);

    Task<PriorAuthMetrics?> GetMetricsAsync(DateTime from, DateTime to);
}

public sealed class Cms0057Service : ICms0057Service
{
    public const string ServiceName = "Authorization Service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Cms0057Service> _logger;

    public Cms0057Service(HttpClient http, IConfiguration configuration, ILogger<Cms0057Service> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl =>
        (_configuration["Services:AuthorizationService"] ?? "http://authorization-service.cloudhealthoffice/api").TrimEnd('/');

    public async Task<List<PriorAuthClock>> GetDecisionClocksAsync() =>
        await GetAsync<List<PriorAuthClock>>("authorizations/sla/at-risk?minLevel=None") ?? new();

    public async Task<List<PriorAuthDecision>> GetRecentDecisionsAsync(int days = 30, int limit = 100) =>
        await GetAsync<List<PriorAuthDecision>>($"authorizations/cms-0057/decisions?days={days}&limit={limit}") ?? new();

    public Task<PriorAuthMetrics?> GetMetricsAsync(DateTime from, DateTime to) =>
        GetAsync<PriorAuthMetrics>(
            $"authorizations/cms-0057/metrics?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");

    private async Task<T?> GetAsync<T>(string path)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>($"{BaseUrl}/{path}", Json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}

/// <summary>An open request's decision clock (authorization-service AuthorizationSlaStatus).</summary>
public sealed class PriorAuthClock
{
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? LevelOfService { get; set; }
    public DateTime SlaStartedAt { get; set; }
    public DateTime SlaDeadline { get; set; }
    public double HoursRemaining { get; set; }
    public double HoursElapsed { get; set; }
    public double PercentConsumed { get; set; }
    public string EscalationLevel { get; set; } = "None";

    [JsonIgnore] public bool Expedited => string.Equals(LevelOfService, "U", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string RequestType => Expedited ? "Expedited (72 h)" : "Standard (7 days)";
    [JsonIgnore] public bool Breached => EscalationLevel == "Breach" || HoursRemaining <= 0;
}

public sealed class PriorAuthDecision
{
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? RequestingProviderName { get; set; }
    public string RequestType { get; set; } = "Standard";
    public string Status { get; set; } = string.Empty;
    public DateTime SubmittedDate { get; set; }
    public DateTime DecidedDate { get; set; }
    public double? DecisionHours { get; set; }
    public double TimeframeHours { get; set; }
    public bool WithinTimeframe { get; set; }
    public bool Automated { get; set; }
    public string? DecidedBy { get; set; }
    public string? Reason { get; set; }
    public string? DenialReasonCode { get; set; }

    /// <summary>"Approved with changes" reads better than the 278 term Modified.</summary>
    [JsonIgnore] public string Outcome => Status == "Modified" ? "Approved with changes" : Status;

    /// <summary>The rule name for an automated decision, without its lob/state qualifiers.</summary>
    [JsonIgnore] public string Decider =>
        Automated
            ? "Automated: " + (DecidedBy ?? "automated").Split(';')[0].Replace("automated:", "", StringComparison.OrdinalIgnoreCase)
            : DecidedBy ?? "Reviewer";
}

public sealed class PriorAuthMetrics
{
    public string ReportingBasis { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TotalRequests { get; set; }
    public int OpenRequests { get; set; }
    public int DecidedRequests { get; set; }
    public int AutomatedDecisions { get; set; }
    public PriorAuthRequestTypeMetrics Standard { get; set; } = new();
    public PriorAuthRequestTypeMetrics Expedited { get; set; } = new();
    public List<PriorAuthMetricGap> NotYetTracked { get; set; } = new();
}

public sealed class PriorAuthRequestTypeMetrics
{
    public double TimeframeHours { get; set; }
    public int Requests { get; set; }
    public int Decided { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    public int DecisionTimingUnavailable { get; set; }
    public double? ApprovedPercent { get; set; }
    public double? DeniedPercent { get; set; }
    public double? AverageDecisionHours { get; set; }
    public double? MedianDecisionHours { get; set; }
    public int DecidedWithinTimeframe { get; set; }
    public double? DecidedWithinTimeframePercent { get; set; }
}

public sealed class PriorAuthMetricGap
{
    public string Metric { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
