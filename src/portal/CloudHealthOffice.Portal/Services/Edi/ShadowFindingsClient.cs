using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The recovery worklist (claims-service <c>shadow-adjudication/findings</c>):
/// payment differences from shadow runs, the plan's review of each, provider
/// roll-ups, the recoupment lookback and the mass-adjustment CSV. Reading needs
/// <c>claims:import</c>; changing a status or the lookback needs
/// <c>claims:adjust</c>. Calls carry the signed-in user's CHO token.
/// </summary>
public interface IShadowFindingsClient
{
    Task<List<ShadowFindingView>> ListAsync(string? status, string? direction, string? lookback, int limit = 200);
    Task<ShadowFindingSummary?> GetSummaryAsync(int topProviders = 25);
    Task<DataDropChange<ShadowFindingView>> SetStatusAsync(string id, ShadowFindingStatusRequest request);
    Task<ShadowRecoveryLookback?> GetLookbackAsync();
    Task<DataDropChange<ShadowRecoveryLookback>> SaveLookbackAsync(ShadowRecoveryLookback lookback);

    /// <summary>The worklist CSV for the given statuses; null when the service refused it.</summary>
    Task<byte[]?> GetExportCsvAsync(IReadOnlyList<string> statuses);
}

public class ShadowFindingsClient : IShadowFindingsClient
{
    private const string ServiceName = "Claims Service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShadowFindingsClient> _logger;

    public ShadowFindingsClient(HttpClient httpClient, IConfiguration configuration, ILogger<ShadowFindingsClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string Base => $"{_configuration["Services:ClaimsService"]}/v1/claims/shadow-adjudication/findings";

    public async Task<List<ShadowFindingView>> ListAsync(string? status, string? direction, string? lookback, int limit = 200)
    {
        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 1000)}" };
        if (!string.IsNullOrEmpty(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrEmpty(direction)) query.Add($"direction={Uri.EscapeDataString(direction)}");
        if (!string.IsNullOrEmpty(lookback)) query.Add($"lookback={Uri.EscapeDataString(lookback)}");
        return await GetAsync<List<ShadowFindingView>>($"{Base}?{string.Join("&", query)}") ?? new();
    }

    public Task<ShadowFindingSummary?> GetSummaryAsync(int topProviders = 25)
        => GetAsync<ShadowFindingSummary>($"{Base}/summary?topProviders={Math.Clamp(topProviders, 1, 500)}");

    public Task<DataDropChange<ShadowFindingView>> SetStatusAsync(string id, ShadowFindingStatusRequest request)
        => SendAsync<ShadowFindingView>(HttpMethod.Post, $"{Base}/{Uri.EscapeDataString(id)}/status", request,
            "You don't have permission to review findings (claims:adjust).");

    public Task<ShadowRecoveryLookback?> GetLookbackAsync() => GetAsync<ShadowRecoveryLookback>($"{Base}/lookback");

    public Task<DataDropChange<ShadowRecoveryLookback>> SaveLookbackAsync(ShadowRecoveryLookback lookback)
        => SendAsync<ShadowRecoveryLookback>(HttpMethod.Put, $"{Base}/lookback", lookback,
            "You don't have permission to change the recoupment lookback (claims:adjust).");

    public async Task<byte[]?> GetExportCsvAsync(IReadOnlyList<string> statuses)
    {
        var query = string.Join("&", statuses.Select(s => $"status={Uri.EscapeDataString(s)}"));
        try
        {
            using var response = await _httpClient.GetAsync($"{Base}/export.csv{(query.Length > 0 ? "?" + query : "")}");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{ServiceName} answered {(int)response.StatusCode}.");
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private async Task<T?> GetAsync<T>(string url) where T : class
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{ServiceName} answered {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private async Task<DataDropChange<T>> SendAsync<T>(HttpMethod method, string url, object body, string forbidden) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: JsonOptions) };
            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions) is { } value
                    ? DataDropChange<T>.Ok(value)
                    : DataDropChange<T>.Refused($"{ServiceName} sent an empty answer.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return DataDropChange<T>.Refused(forbidden);
            return DataDropChange<T>.Refused(await ErrorsAsync(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    return errors.EnumerateArray().Select(e => e.ToString()).ToArray();
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    return [error.GetString()!];
            }
        }
        catch (JsonException)
        {
        }
        return [$"{ServiceName} answered {(int)response.StatusCode}."];
    }
}

// ── models (claims-service shapes) ────────────────────────────────────────

public static class ShadowFindingStatuses
{
    public const string New = "New";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string InRecovery = "InRecovery";
    public const string Recovered = "Recovered";
    public const string Appealed = "Appealed";

    public static readonly IReadOnlyList<string> All = [New, Approved, Rejected, InRecovery, Recovered, Appealed];

    public static string Label(string status) => status switch
    {
        InRecovery => "In recovery",
        New => "New",
        _ => status,
    };
}

public class ShadowFinding
{
    public string Id { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string? SourceClaimId { get; set; }
    public string ClaimType { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public DateTime? CorePaidDate { get; set; }
    public string? BillingProviderNpi { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public decimal CorePaid { get; set; }
    public decimal ChoPaid { get; set; }
    public decimal Amount { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }
    public string? AuthorizationFinding { get; set; }
    public string RunId { get; set; } = string.Empty;
    public bool StillDiffers { get; set; } = true;
    public string Status { get; set; } = ShadowFindingStatuses.New;
    public DateTime? StatusChangedAtUtc { get; set; }
    public string? StatusChangedBy { get; set; }
    public string? Note { get; set; }
    public decimal? RecoveredAmount { get; set; }
}

public class ShadowFindingView
{
    public ShadowFinding Finding { get; set; } = new();
    public string LookbackState { get; set; } = "Unknown";
    public int? LookbackDays { get; set; }
    public DateTime? LookbackDeadline { get; set; }
    public int? DaysLeft { get; set; }
    public string? ContractNumber { get; set; }
    public List<string> NextStatuses { get; set; } = new();
}

public class ShadowFindingStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public decimal? RecoveredAmount { get; set; }
}

public class ShadowFindingTotal
{
    public int Findings { get; set; }
    public decimal Amount { get; set; }
    public decimal Recovered { get; set; }
}

public class ShadowFindingStatusRow
{
    public string Status { get; set; } = string.Empty;
    public int Findings { get; set; }
    public decimal Amount { get; set; }
    public decimal Recovered { get; set; }
}

public class ShadowFindingProviderRow
{
    public string BillingProviderNpi { get; set; } = string.Empty;
    public string? ContractNumber { get; set; }
    public int Findings { get; set; }
    public decimal CorePaidMore { get; set; }
    public decimal CorePaidLess { get; set; }
    public decimal OpenOverpayments { get; set; }
    public decimal Confirmed { get; set; }
    public decimal Recovered { get; set; }
    public int PastLookback { get; set; }
    public DateTime? NextDeadline { get; set; }
}

public class ShadowFindingSummary
{
    public int Findings { get; set; }
    public ShadowFindingTotal Overpayments { get; set; } = new();
    public ShadowFindingTotal Underpayments { get; set; } = new();
    public ShadowFindingTotal Confirmed { get; set; } = new();
    public ShadowFindingTotal Recovered { get; set; } = new();
    public decimal? RecoveryRate { get; set; }
    public List<ShadowFindingStatusRow> ByStatus { get; set; } = new();
    public ShadowFindingTotal PastLookback { get; set; } = new();
    public ShadowFindingTotal LookbackDueSoon { get; set; } = new();
    public ShadowFindingTotal LookbackUnknown { get; set; } = new();
    public List<ShadowFindingProviderRow> ByProvider { get; set; } = new();
}

public class ShadowRecoveryLookback
{
    public int? DefaultDays { get; set; }
    public List<ShadowProviderLookback> Providers { get; set; } = new();
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class ShadowProviderLookback
{
    public string Npi { get; set; } = string.Empty;
    public string? ContractNumber { get; set; }
    public int Days { get; set; }
}
