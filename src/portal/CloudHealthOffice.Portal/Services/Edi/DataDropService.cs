using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The tenant's daily file drop, for the Imports &amp; EDI console: the runs
/// (claims-service, <c>claims:import</c>), the freshness and reconciliation
/// report, the drop settings, loading now, and the hosted SFTP login
/// (tenant-service, <c>settings:manage</c>). Calls carry the signed-in user's
/// CHO token, so each service enforces its own permission and tenant.
/// </summary>
public interface IDataDropService
{
    Task<List<DataDropRunRecord>> GetRunsAsync(int limit = 30);
    Task<DataDropFreshnessReport?> GetReportAsync(int days = 30);
    Task<DataDropSettingsView?> GetSettingsAsync();
    Task<DataDropChange<DataDropSettingsView>> SaveSettingsAsync(DataDropSettings settings);

    /// <summary>Loads what is waiting now. Null value with no errors: nothing was ready.</summary>
    Task<DataDropChange<DataDropRunRecord>> RunNowAsync();

    Task<DataDropSftpAccount?> GetSftpAccountAsync();
    Task<DataDropChange<DataDropSftpAccount>> SetSftpKeysAsync(IReadOnlyList<string> publicKeys);
    Task<DataDropChange<DataDropSftpAccount>> DisableSftpAsync();
}

public class DataDropService : IDataDropService
{
    private const string ClaimsServiceName = "Claims Service";
    private const string TenantServiceName = "Tenant Service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ITenantContextService _tenantContext;
    private readonly ILogger<DataDropService> _logger;

    public DataDropService(HttpClient httpClient, IConfiguration configuration, ITenantContextService tenantContext,
        ILogger<DataDropService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private string DropsUrl => $"{_configuration["Services:ClaimsService"]}/v1/claims/import/cpcds/drops";

    public async Task<List<DataDropRunRecord>> GetRunsAsync(int limit = 30)
        => await GetAsync<List<DataDropRunRecord>>($"{DropsUrl}?limit={Math.Clamp(limit, 1, 200)}", ClaimsServiceName) ?? new();

    public Task<DataDropFreshnessReport?> GetReportAsync(int days = 30)
        => GetAsync<DataDropFreshnessReport>($"{DropsUrl}/report?days={Math.Clamp(days, 1, 366)}", ClaimsServiceName);

    public Task<DataDropSettingsView?> GetSettingsAsync()
        => GetAsync<DataDropSettingsView>($"{DropsUrl}/settings", ClaimsServiceName);

    public Task<DataDropChange<DataDropSettingsView>> SaveSettingsAsync(DataDropSettings settings)
        => SendAsync<DataDropSettingsView>(HttpMethod.Put, $"{DropsUrl}/settings", settings, ClaimsServiceName);

    public Task<DataDropChange<DataDropRunRecord>> RunNowAsync()
        => SendAsync<DataDropRunRecord>(HttpMethod.Post, $"{DropsUrl}/run", null, ClaimsServiceName);

    public async Task<DataDropSftpAccount?> GetSftpAccountAsync()
        => await SftpUrlAsync() is { } url ? await GetAsync<DataDropSftpAccount>(url, TenantServiceName) : null;

    public async Task<DataDropChange<DataDropSftpAccount>> SetSftpKeysAsync(IReadOnlyList<string> publicKeys)
        => await SftpUrlAsync() is { } url
            ? await SendAsync<DataDropSftpAccount>(HttpMethod.Put, url, new { publicKeys }, TenantServiceName)
            : DataDropChange<DataDropSftpAccount>.Refused("No tenant is selected.");

    public async Task<DataDropChange<DataDropSftpAccount>> DisableSftpAsync()
        => await SftpUrlAsync() is { } url
            ? await SendAsync<DataDropSftpAccount>(HttpMethod.Delete, url, null, TenantServiceName)
            : DataDropChange<DataDropSftpAccount>.Refused("No tenant is selected.");

    private async Task<string?> SftpUrlAsync()
        => await _tenantContext.GetTenantIdAsync() is { Length: > 0 } tenantId
            ? $"{_configuration["Services:TenantService"]}/v1/tenants/{Uri.EscapeDataString(tenantId)}/cpcds-sftp"
            : null;

    /// <summary>The resource, or null when the service answers 404 or 403 (not set up, or not this user's to see).</summary>
    private async Task<T?> GetAsync<T>(string url, string serviceName) where T : class
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{serviceName} answered {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", serviceName);
            throw new ServiceUnavailableException(serviceName, ex);
        }
    }

    private async Task<DataDropChange<T>> SendAsync<T>(HttpMethod method, string url, object? body, string serviceName) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new DataDropChange<T>(null, Array.Empty<string>());
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions) is { } value
                    ? DataDropChange<T>.Ok(value)
                    : DataDropChange<T>.Refused($"{serviceName} sent an empty answer.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return DataDropChange<T>.Refused("You don't have permission to make this change.");
            return DataDropChange<T>.Refused(await ErrorsAsync(response, serviceName));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", serviceName);
            throw new ServiceUnavailableException(serviceName, ex);
        }
    }

    /// <summary>The services answer <c>{ error, errors?, detail? }</c>; anything else becomes the status code.</summary>
    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response, string serviceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    return errors.EnumerateArray().Select(e => e.ToString()).Where(e => e.Length > 0).ToArray();
                var text = new[] { "detail", "error" }
                    .Select(p => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (text is not null)
                    return [text];
            }
        }
        catch (JsonException)
        {
        }
        return [$"{serviceName} answered {(int)response.StatusCode}."];
    }
}
