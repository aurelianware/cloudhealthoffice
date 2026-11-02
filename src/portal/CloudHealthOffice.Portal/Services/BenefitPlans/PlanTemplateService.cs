using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The guided plan onboarding flow: browse the state program templates
/// (benefit-plan-service <c>/api/v1/plans/templates</c>), create a Draft plan
/// from one, edit the Draft and publish it. Drafts are edited as the raw plan
/// JSON so fields the portal does not show are sent back unchanged.
/// </summary>
public interface IPlanTemplateService
{
    Task<List<PlanTemplateSummary>> ListTemplatesAsync(string? state = null, string? program = null);
    Task<PlanTemplateDetail?> GetTemplateAsync(string templateId);
    Task<PlanWriteResult> CreateDraftFromTemplateAsync(string templateId, CreateDraftFromTemplateRequest request);

    /// <summary>Every Draft version in the tenant, newest first.</summary>
    Task<List<PlanDraftSummary>> ListDraftsAsync();
    Task<JsonObject?> GetVersionAsync(string planId, string versionId);
    Task<PlanWriteResult> SaveDraftAsync(JsonObject draft);
    Task<PlanWriteResult> PublishAsync(string planId, string versionId, DateTime? effectiveDate = null);
}

public sealed class PlanTemplateService : IPlanTemplateService
{
    private const string ServiceName = "Benefit Plan Service";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlanTemplateService> _logger;

    public PlanTemplateService(HttpClient http, IConfiguration configuration, ILogger<PlanTemplateService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => $"{_configuration["Services:BenefitPlanService"]}/v1/plans";

    public async Task<List<PlanTemplateSummary>> ListTemplatesAsync(string? state = null, string? program = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(state)) query.Add($"state={Uri.EscapeDataString(state)}");
        if (!string.IsNullOrWhiteSpace(program)) query.Add($"program={Uri.EscapeDataString(program)}");
        var url = $"{BaseUrl}/templates" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        return await Call(async () => await _http.GetFromJsonAsync<List<PlanTemplateSummary>>(url, Json) ?? new());
    }

    public Task<PlanTemplateDetail?> GetTemplateAsync(string templateId) => Call(async () =>
    {
        using var response = await _http.GetAsync($"{BaseUrl}/templates/{Uri.EscapeDataString(templateId)}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TemplateDetailResponse>(Json);
        return body?.Template;
    });

    public Task<PlanWriteResult> CreateDraftFromTemplateAsync(string templateId, CreateDraftFromTemplateRequest request) =>
        Write(() => _http.PostAsJsonAsync($"{BaseUrl}/templates/{Uri.EscapeDataString(templateId)}/drafts", request, Json));

    public Task<List<PlanDraftSummary>> ListDraftsAsync() => Call(async () =>
    {
        var plans = await _http.GetFromJsonAsync<List<PlanDraftSummary>>($"{BaseUrl}?activeOnly=false", Json) ?? new();
        return plans
            .Where(p => string.Equals(p.VersionState, "Draft", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
    });

    /// <summary>
    /// Read from the version list, not <c>GET /versions/{id}</c>: the list
    /// returns the stored row, while the single-version read goes through the
    /// vendor adapter DTO, which drops fields (plan year definition,
    /// accumulator targets) that a save would then erase.
    /// </summary>
    public Task<JsonObject?> GetVersionAsync(string planId, string versionId) => Call(async () =>
    {
        using var response = await _http.GetAsync($"{BaseUrl}/{Uri.EscapeDataString(planId)}/versions?pageSize=200");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var page = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
        return (page?["items"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(v => string.Equals(v["versionId"]?.ToString(), versionId, StringComparison.Ordinal))?
            .DeepClone() as JsonObject;
    });

    public Task<PlanWriteResult> SaveDraftAsync(JsonObject draft)
    {
        var id = draft["id"]?.GetValue<string>() ?? string.Empty;
        return Write(() => _http.PutAsync(
            $"{BaseUrl}/{Uri.EscapeDataString(id)}",
            new StringContent(draft.ToJsonString(), System.Text.Encoding.UTF8, "application/json")));
    }

    public Task<PlanWriteResult> PublishAsync(string planId, string versionId, DateTime? effectiveDate = null) =>
        Write(() => _http.PostAsJsonAsync(
            $"{BaseUrl}/{Uri.EscapeDataString(planId)}/versions/{Uri.EscapeDataString(versionId)}/publish",
            new { effectiveDate }, Json));

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

    /// <summary>
    /// A write's 400 and 409 carry a message the user can act on (plan id
    /// taken, plan limits invalid, version no longer a Draft), so they come
    /// back as a failed result rather than an exception.
    /// </summary>
    private Task<PlanWriteResult> Write(Func<Task<HttpResponseMessage>> send) => Call(async () =>
    {
        using var response = await send();
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            var node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
            return PlanWriteResult.Ok(
                node?["planId"]?.GetValue<string>() ?? string.Empty,
                node?["versionId"]?.GetValue<string>() ?? string.Empty);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict
            or HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return PlanWriteResult.Fail(response.StatusCode == HttpStatusCode.Forbidden
                ? "You need the settings:manage permission to change plans."
                : ReadMessage(text) ?? $"The request was refused ({(int)response.StatusCode}).");
        }

        response.EnsureSuccessStatusCode();
        return PlanWriteResult.Fail("Unexpected response.");
    });

    private static string? ReadMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o) return null;
            if (o["message"] is JsonValue m) return m.GetValue<string>();
            if (o["errors"] is JsonObject errors)
                return string.Join(" ", errors.Select(e => e.Value is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString())) : e.Value?.ToString()));
            return o["title"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class TemplateDetailResponse
    {
        [JsonPropertyName("template")] public PlanTemplateDetail? Template { get; set; }
    }
}

public sealed record PlanWriteResult(bool Success, string PlanId, string VersionId, string? Error)
{
    public static PlanWriteResult Ok(string planId, string versionId) => new(true, planId, versionId, null);
    public static PlanWriteResult Fail(string error) => new(false, string.Empty, string.Empty, error);
}

public sealed class PlanTemplateSummary
{
    public string TemplateId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Population { get; set; } = string.Empty;
    public int VerifyCount { get; set; }
    public int NotModeledCount { get; set; }
}

public sealed class PlanTemplateDetail
{
    public string TemplateId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Population { get; set; } = string.Empty;
    public int?[]? AgeRange { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public string? EffectiveFrom { get; set; }
    public string? Notes { get; set; }
    public PlanTemplateCostSharing CostSharing { get; set; } = new();
    public List<PlanTemplateBenefit> Benefits { get; set; } = new();
    public List<PlanTemplateBenefit> DisplayBenefits { get; set; } = new();
    public List<PlanTemplateNote> CarveOuts { get; set; } = new();
    public List<PlanTemplateNote> NotModeled { get; set; } = new();
    public Dictionary<string, PlanTemplateSource> Sources { get; set; } = new();

    /// <summary>The cited source for a value, or null when it has none or cites an unknown key.</summary>
    public PlanTemplateSource? SourceFor(string? key) =>
        key is not null && Sources.TryGetValue(key, out var s) ? s : null;

    /// <summary>The template's row for a service category, matched the way the Draft was built.</summary>
    public PlanTemplateBenefit? BenefitFor(string serviceCategory) =>
        Benefits.Concat(DisplayBenefits).FirstOrDefault(b =>
            string.Equals(b.ServiceCategory, serviceCategory, StringComparison.OrdinalIgnoreCase));
}

public sealed class PlanTemplateCostSharing
{
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOutOfPocketMax { get; set; }
    public decimal FamilyOutOfPocketMax { get; set; }
    public decimal Coinsurance { get; set; }
    public decimal MonthlyPremium { get; set; }
}

public sealed class PlanTemplateBenefit
{
    public string ServiceCategory { get; set; } = string.Empty;
    public bool Covered { get; set; } = true;
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public decimal? AnnualMaximum { get; set; }
    public string? Limitations { get; set; }
    public string? FormularyTier { get; set; }
    public string? Source { get; set; }
    public string Status { get; set; } = "verify";

    public bool IsVerify => string.Equals(Status, "verify", StringComparison.OrdinalIgnoreCase);
}

public sealed class PlanTemplateNote
{
    public string? Item { get; set; }
    public string? Service { get; set; }
    public string? To { get; set; }
    public string? Why { get; set; }
    public string? Source { get; set; }
    public string Status { get; set; } = "verify";

    public bool IsVerify => string.Equals(Status, "verify", StringComparison.OrdinalIgnoreCase);
}

public sealed class PlanTemplateSource
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Accessed { get; set; }
}

public sealed class CreateDraftFromTemplateRequest
{
    public string PlanId { get; set; } = string.Empty;
    public string? PlanName { get; set; }
    public string Payer { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; } = new(DateTime.UtcNow.Year + 1, 1, 1);
    public DateTime? TerminationDate { get; set; }
    public string? ProgramId { get; set; }
}

public sealed class PlanDraftSummary
{
    public string Id { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public string VersionState { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime CreatedAt { get; set; }
}
