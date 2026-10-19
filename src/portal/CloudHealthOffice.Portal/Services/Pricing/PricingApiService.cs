using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class PricingApiService : IPricingApiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PricingApiService> _logger;

    public PricingApiService(HttpClient httpClient, IConfiguration configuration, ILogger<PricingApiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:PricingApi"] ?? "http://pricing-api.cloudhealthoffice";
    private string AdminSecret => _configuration["PricingApi:AdminSecret"] ?? "";

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Admin-Secret", AdminSecret);
        return request;
    }

    public async Task<List<PricingApiKey>> GetApiKeysAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Get, $"{BaseUrl}/api/v1/admin/api-keys");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var keys = await ReadDataAsync<List<PricingApiKey>>(response);
            return keys ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<PricingApiKey> CreateApiKeyAsync(string tenantName, string contactEmail, string tier)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys");
            request.Content = JsonContent.Create(new { tenantName, contactEmail, tier });
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            // The create response is { apiKey, key: { keyId, keyPrefix, ... } }:
            // the key is shown this once and never again.
            var created = await ReadDataAsync<PricingApiKeyCreated>(response);
            if (created?.Key is { } key)
            {
                key.ApiKey = created.ApiKey;
                return key;
            }
            return new PricingApiKey { ApiKey = created?.ApiKey ?? "" };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    private sealed class PricingApiKeyCreated
    {
        public string ApiKey { get; set; } = "";
        public PricingApiKey? Key { get; set; }
    }

    /// <summary>Reads the Pricing API's <c>{ success, data }</c> envelope (or a bare body).</summary>
    private static async Task<T?> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var element = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data)
            ? data
            : doc.RootElement;
        return element.Deserialize<T>(options);
    }

    public async Task DeactivateApiKeyAsync(string keyId)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Delete, $"{BaseUrl}/api/v1/admin/api-keys/{Uri.EscapeDataString(keyId)}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task ResetUsageAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys/reset-usage");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<List<PricingFeeScheduleInfo>> GetFeeSchedulesAsync()
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PricingFeeScheduleInfo>>($"{BaseUrl}/api/v1/fee-schedules");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<FeeScheduleUploadResult> UploadFeeScheduleAsync(string type, int year, Stream csvStream, string fileName, decimal? baseRate = null)
    {
        try
        {
            var url = $"{BaseUrl}/api/v1/admin/fee-schedules/upload/{type.ToLowerInvariant()}?year={year}";
            if (baseRate.HasValue)
                url += $"&baseRate={baseRate.Value}";

            var request = CreateAdminRequest(HttpMethod.Post, url);
            var content = new MultipartFormDataContent();
            content.Add(new StreamContent(csvStream), "file", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<FeeScheduleUploadResult>();
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task SeedDemoDataAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/fee-schedules/seed-demo");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }
}
