using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// A module that serves standard reports: where its catalog lives, the
/// permissions that let a user run its reports, and the line-of-business
/// values its service accepts.
/// </summary>
public sealed record ReportModule(
    string Key,
    string Title,
    string ServiceName,
    string BaseUrlSetting,
    string Path,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> LinesOfBusiness);

public static class ReportModules
{
    private static readonly string[] CoreLines = ["Commercial", "Medicare", "Medicaid", "Exchange", "TRICARE", "VA"];

    public static readonly ReportModule Claims = new(
        "claims", "Claims", "Claims Service", "Services:ClaimsService", "claims/reports", ["reports:claims"], CoreLines);

    public static readonly ReportModule UtilizationManagement = new(
        "um", "Prior authorization", "Authorization Service", "Services:AuthorizationService", "authorizations/reports",
        ["reports:um", "reports:compliance"], CoreLines);

    public static readonly ReportModule AppealsAndGrievances = new(
        "appeals", "Appeals and grievances", "Appeals Service", "Services:AppealsService", "appeals/reports",
        ["reports:um", "reports:compliance"], ["Commercial", "Medicare", "Medicaid", "Marketplace", "MedicarePartD"]);

    public static readonly IReadOnlyList<ReportModule> All = [Claims, UtilizationManagement, AppealsAndGrievances];
}

/// <summary>Lists, runs and downloads the modules' standard reports (portal-reporting.md layer 2).</summary>
public interface IReportsService
{
    /// <summary>The module's reports; empty when the service refuses the caller.</summary>
    Task<List<ReportDefinitionView>> GetCatalogAsync(ReportModule module);

    /// <summary>Runs a report; <see cref="ReportForbiddenException"/> on 403, <see cref="ArgumentException"/> on 400.</summary>
    Task<ReportRunView> RunAsync(ReportModule module, string reportId, IReadOnlyDictionary<string, string?> parameters);

    /// <summary>The same run as the service's CSV file.</summary>
    Task<ReportFile> DownloadCsvAsync(ReportModule module, string reportId, IReadOnlyDictionary<string, string?> parameters);
}

public sealed class ReportsService : IReportsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportsService> _logger;

    public ReportsService(HttpClient httpClient, IConfiguration configuration, ILogger<ReportsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl(ReportModule module) => $"{_configuration[module.BaseUrlSetting]?.TrimEnd('/')}/{module.Path}";

    public async Task<List<ReportDefinitionView>> GetCatalogAsync(ReportModule module)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BaseUrl(module));
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new List<ReportDefinitionView>();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<ReportDefinitionView>>(JsonOptions) ?? new List<ReportDefinitionView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", module.ServiceName);
            throw new ServiceUnavailableException(module.ServiceName, ex);
        }
    }

    public async Task<ReportRunView> RunAsync(ReportModule module, string reportId, IReadOnlyDictionary<string, string?> parameters)
    {
        using var response = await GetReportAsync(module, reportId, parameters, csv: false);
        return await response.Content.ReadFromJsonAsync<ReportRunView>(JsonOptions) ?? new ReportRunView();
    }

    public async Task<ReportFile> DownloadCsvAsync(ReportModule module, string reportId, IReadOnlyDictionary<string, string?> parameters)
    {
        using var response = await GetReportAsync(module, reportId, parameters, csv: true);
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"') ?? $"{reportId}.csv";
        return new ReportFile(fileName, await response.Content.ReadAsByteArrayAsync());
    }

    private async Task<HttpResponseMessage> GetReportAsync(
        ReportModule module, string reportId, IReadOnlyDictionary<string, string?> parameters, bool csv)
    {
        var query = string.Join("&", parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Append(new KeyValuePair<string, string?>("format", csv ? "csv" : null))
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync($"{BaseUrl(module)}/{Uri.EscapeDataString(reportId)}" + (query.Length > 0 ? "?" + query : ""));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", module.ServiceName);
            throw new ServiceUnavailableException(module.ServiceName, ex);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new ReportForbiddenException(reportId);
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var reason = await ReadErrorAsync(response);
            response.Dispose();
            throw new ArgumentException(reason);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ServiceUnavailableException(module.ServiceName, new HttpRequestException($"Report {reportId} failed with {status}."));
        }
        return response;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString() ?? text;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(text) ? "The report parameters were not accepted." : text;
    }
}
