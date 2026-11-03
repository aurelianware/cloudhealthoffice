using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Shadow adjudication runs and reports for the portal's Shadow Adjudication
/// page (claims-service, <c>claims:import</c>). The tenant's on/off switch is
/// on <see cref="IDataDropService"/>. Calls carry the signed-in user's CHO
/// token, so claims-service enforces the permission and the tenant.
/// </summary>
public interface IShadowAdjudicationClient
{
    Task<List<ShadowRun>> ListRunsAsync(int limit = 25);
    Task<ShadowRunReport?> GetReportAsync(string runId, int top = 10);
    Task<List<ShadowResult>> ListResultsAsync(string runId, string? kind, int limit = 50);

    /// <summary>Queues a background shadow run over every claim of one CPCDS import batch.</summary>
    Task<DataDropChange<ShadowRun>> RunBatchAsync(string importBatchId);

    /// <summary>The run's discrepancy CSV; null when the run is not this tenant's.</summary>
    Task<byte[]?> GetDiscrepanciesCsvAsync(string runId, bool includeUndecided);
}

public class ShadowAdjudicationClient : IShadowAdjudicationClient
{
    private const string ServiceName = "Claims Service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShadowAdjudicationClient> _logger;

    public ShadowAdjudicationClient(HttpClient httpClient, IConfiguration configuration, ILogger<ShadowAdjudicationClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string RunsUrl => $"{_configuration["Services:ClaimsService"]}/v1/claims/shadow-adjudication/runs";

    private string RunUrl(string runId) => $"{RunsUrl}/{Uri.EscapeDataString(runId)}";

    public async Task<List<ShadowRun>> ListRunsAsync(int limit = 25)
        => await GetAsync<List<ShadowRun>>($"{RunsUrl}?limit={Math.Clamp(limit, 1, 200)}") ?? new();

    public Task<ShadowRunReport?> GetReportAsync(string runId, int top = 10)
        => GetAsync<ShadowRunReport>($"{RunUrl(runId)}/report?top={Math.Clamp(top, 1, 100)}");

    public async Task<List<ShadowResult>> ListResultsAsync(string runId, string? kind, int limit = 50)
    {
        var url = $"{RunUrl(runId)}/results?limit={Math.Clamp(limit, 1, 1000)}";
        if (!string.IsNullOrEmpty(kind))
            url += $"&kind={Uri.EscapeDataString(kind)}";
        return await GetAsync<List<ShadowResult>>(url) ?? new();
    }

    public async Task<DataDropChange<ShadowRun>> RunBatchAsync(string importBatchId)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(RunsUrl, new { importBatchId, background = true }, JsonOptions);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<ShadowRun>(JsonOptions) is { } run
                    ? DataDropChange<ShadowRun>.Ok(run)
                    : DataDropChange<ShadowRun>.Refused($"{ServiceName} sent an empty answer.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return DataDropChange<ShadowRun>.Refused("You don't have permission to run shadow adjudication.");
            return DataDropChange<ShadowRun>.Refused(await ErrorAsync(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    public async Task<byte[]?> GetDiscrepanciesCsvAsync(string runId, bool includeUndecided)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{RunUrl(runId)}/discrepancies.csv?includeUndecided={(includeUndecided ? "true" : "false")}");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
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

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                return error.GetString()!;
        }
        catch (JsonException)
        {
        }
        return $"{ServiceName} answered {(int)response.StatusCode}.";
    }
}
