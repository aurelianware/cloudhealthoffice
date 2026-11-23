using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The pended-claims report (claims-service
/// <c>shadow-adjudication/pended</c>, <c>claims:import</c>): for each pend
/// reason in the plan's core, what CHO would have done and whether it agreed
/// with the examiner. Calls carry the signed-in user's CHO token.
/// </summary>
public interface IShadowPendedClient
{
    /// <summary>The report over the last <paramref name="days"/> days; null when the service refused it.</summary>
    Task<ShadowPendedAnalysis?> GetAsync(int days = 365);
}

public class ShadowPendedClient : IShadowPendedClient
{
    private const string ServiceName = "Claims Service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShadowPendedClient> _logger;

    public ShadowPendedClient(HttpClient httpClient, IConfiguration configuration, ILogger<ShadowPendedClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ShadowPendedAnalysis?> GetAsync(int days = 365)
    {
        var url = $"{_configuration["Services:ClaimsService"]}/v1/claims/shadow-adjudication/pended?days={Math.Clamp(days, 1, 3660)}";
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{ServiceName} answered {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<ShadowPendedAnalysis>(JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}

// ── models (claims-service shapes) ────────────────────────────────────────

public class ShadowPendedAnalysis
{
    public DateTime AsOfUtc { get; set; }
    public DateTime SinceUtc { get; set; }
    public bool Truncated { get; set; }
    public ShadowPendReasonRow Total { get; set; } = new();
    public List<ShadowPendReasonRow> ByPendReason { get; set; } = new();
}

public class ShadowPendReasonRow
{
    public string PendReasonCode { get; set; } = string.Empty;
    public string? PendReason { get; set; }
    public int Claims { get; set; }
    public int ChoWouldResolve { get; set; }
    public int ChoWouldPay { get; set; }
    public int ChoWouldDeny { get; set; }
    public int ChoWouldPend { get; set; }
    public decimal? AutoResolveRate { get; set; }
    public List<string> ChoPendCodes { get; set; } = new();
    public int ExaminerDecided { get; set; }
    public int Compared { get; set; }
    public int Agreed { get; set; }
    public int ChoPaidExaminerDenied { get; set; }
    public int ChoDeniedExaminerPaid { get; set; }
    public int PaidAmountDiffers { get; set; }
    public decimal? AgreementRate { get; set; }
    public decimal? AverageDaysPended { get; set; }
    public int StillPended { get; set; }
    public int? OldestStillPendedDays { get; set; }
    public int Voided { get; set; }
    public bool AutomationCandidate { get; set; }
}
