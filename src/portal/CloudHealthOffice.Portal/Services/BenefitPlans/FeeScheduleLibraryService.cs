using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The shared fee schedule library and the tenant's own schedules
/// (benefit-plan-service <c>/api/v1/fee-schedule-library</c> and
/// <c>/api/v1/fee-schedules</c>): browse, open a schedule's rates, preview
/// and deploy a copy at a percentage.
/// </summary>
public interface IFeeScheduleLibraryService
{
    Task<List<FeeScheduleSummaryView>> ListLibraryAsync(string? jurisdiction = null);
    Task<List<FeeScheduleSummaryView>> ListTenantAsync();
    Task<FeeScheduleDetailView?> GetAsync(bool library, string id, string? code, int skip, int take);

    /// <summary>Dry run or deploy; a refused deploy comes back with <see cref="FeeScheduleDeployView.Error"/> set.</summary>
    Task<FeeScheduleDeployView> DeployAsync(FeeScheduleDeployInput input);
}

public sealed class FeeScheduleLibraryService : IFeeScheduleLibraryService
{
    private const string ServiceName = "Benefit Plan Service";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FeeScheduleLibraryService> _logger;

    public FeeScheduleLibraryService(HttpClient http, IConfiguration configuration, ILogger<FeeScheduleLibraryService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string Base => $"{_configuration["Services:BenefitPlanService"]}/v1";

    public Task<List<FeeScheduleSummaryView>> ListLibraryAsync(string? jurisdiction = null) => Call(async () =>
        await _http.GetFromJsonAsync<List<FeeScheduleSummaryView>>(
            $"{Base}/fee-schedule-library" + (string.IsNullOrWhiteSpace(jurisdiction) ? "" : $"?jurisdiction={Uri.EscapeDataString(jurisdiction)}"),
            Json) ?? new());

    public Task<List<FeeScheduleSummaryView>> ListTenantAsync() => Call(async () =>
        await _http.GetFromJsonAsync<List<FeeScheduleSummaryView>>($"{Base}/fee-schedules", Json) ?? new());

    public Task<FeeScheduleDetailView?> GetAsync(bool library, string id, string? code, int skip, int take) => Call(async () =>
    {
        var url = $"{Base}/{(library ? "fee-schedule-library" : "fee-schedules")}/schedule?id={Uri.EscapeDataString(id)}&skip={skip}&take={take}"
                  + (string.IsNullOrWhiteSpace(code) ? "" : $"&code={Uri.EscapeDataString(code.Trim())}");
        using var response = await _http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FeeScheduleDetailView>(Json);
    });

    public Task<FeeScheduleDeployView> DeployAsync(FeeScheduleDeployInput input) => Call(async () =>
    {
        using var response = await _http.PostAsJsonAsync($"{Base}/fee-schedule-library/deploy", input, Json);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return new FeeScheduleDeployView { Error = "You need the settings:manage permission to deploy a fee schedule." };
        if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            var view = TryRead(text);
            if (view is not null)
            {
                if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(view.Error))
                    view.Error = $"The deploy was refused ({(int)response.StatusCode}).";
                return view;
            }
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new FeeScheduleDeployView { Error = ReadMessage(text) ?? "The schedule changed while deploying; try again." };
        response.EnsureSuccessStatusCode();
        return new FeeScheduleDeployView { Error = "Unexpected response." };
    });

    private static FeeScheduleDeployView? TryRead(string text)
    {
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<FeeScheduleDeployView>(text, Json); }
        catch (JsonException) { return null; }
    }

    private static string? ReadMessage(string text)
    {
        try { return (JsonNode.Parse(text) as JsonObject)?["message"]?.ToString(); }
        catch (JsonException) { return null; }
    }

    private async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}

public sealed class FeeScheduleSummaryView
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string? Jurisdiction { get; set; }
    public string? Locality { get; set; }
    public string CodeSystem { get; set; } = string.Empty;
    public string LicenseClassification { get; set; } = string.Empty;
    public decimal? ConversionFactor { get; set; }
    public string? DefaultForPlanId { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
}

public sealed class FeeScheduleDetailView
{
    public FeeScheduleSummaryView Schedule { get; set; } = new();
    public int LineCount { get; set; }
    public int MatchingCount { get; set; }
    public List<FeeScheduleLineRow> Lines { get; set; } = new();
}

public sealed class FeeScheduleLineRow
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string? RevenueCode { get; set; }
    public string? Modifier { get; set; }
    public string RateType { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public decimal? FacilityAmount { get; set; }
}

public sealed class FeeScheduleDeployInput
{
    public string SourceId { get; set; } = string.Empty;
    public decimal Percent { get; set; } = 100m;
    public string? PlanId { get; set; }
    public string? Name { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public bool LicenseAcknowledged { get; set; }
    public bool Replace { get; set; }
    public bool DryRun { get; set; } = true;
}

public sealed class FeeScheduleDeployView
{
    public bool DryRun { get; set; }
    public decimal Percent { get; set; }
    public string? Outcome { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string? PlanKey { get; set; }
    public int LineCount { get; set; }
    public bool RequiresCptLicense { get; set; }
    public List<FeeSchedulePreviewRow> Preview { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }

    public bool Written => Outcome is "Created" or "Replaced";
}

public sealed class FeeSchedulePreviewRow
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string? Modifier { get; set; }
    public decimal? LibraryAmount { get; set; }
    public decimal? DeployedAmount { get; set; }
}
