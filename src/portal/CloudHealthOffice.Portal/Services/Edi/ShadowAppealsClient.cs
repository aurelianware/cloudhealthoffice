using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Shadow adjudication for appeals (claims-service
/// <c>shadow-adjudication/claims/{claimNumber}/appeal-evidence</c> and
/// <c>appeal-outcomes</c>): CHO's shadow reading of an appealed claim, shown
/// on the appeal, and the plan's appeal outcomes against it. Advisory only.
/// Evidence needs <c>claims:read</c>; outcomes need <c>claims:import</c>.
/// </summary>
public interface IShadowAppealsClient
{
    /// <summary>The claim's shadow evidence; null when it has not been shadowed or the user may not read it.</summary>
    Task<ShadowAppealEvidence?> GetEvidenceAsync(string claimNumber);

    Task<ShadowAppealSummary?> GetSummaryAsync();

    Task<DataDropChange<ShadowAppealOutcomeImportResult>> ImportOutcomesAsync(Stream file, string fileName, bool dryRun);
}

public class ShadowAppealsClient : IShadowAppealsClient
{
    private const string ServiceName = "Claims Service";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShadowAppealsClient> _logger;

    public ShadowAppealsClient(HttpClient httpClient, IConfiguration configuration, ILogger<ShadowAppealsClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string Base => $"{_configuration["Services:ClaimsService"]}/v1/claims/shadow-adjudication";

    public Task<ShadowAppealEvidence?> GetEvidenceAsync(string claimNumber) =>
        GetAsync<ShadowAppealEvidence>($"{Base}/claims/{Uri.EscapeDataString(claimNumber)}/appeal-evidence");

    public Task<ShadowAppealSummary?> GetSummaryAsync() => GetAsync<ShadowAppealSummary>($"{Base}/appeal-outcomes/summary");

    public async Task<DataDropChange<ShadowAppealOutcomeImportResult>> ImportOutcomesAsync(Stream file, string fileName, bool dryRun)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            form.Add(content, "file", fileName);
            form.Add(new StringContent(dryRun ? "true" : "false"), "dryRun");
            using var response = await _httpClient.PostAsync($"{Base}/appeal-outcomes/import", form);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return DataDropChange<ShadowAppealOutcomeImportResult>.Refused("You don't have permission to load appeal outcomes (claims:import).");
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var result = await response.Content.ReadFromJsonAsync<ShadowAppealOutcomeImportResult>(JsonOptions);
                if (result is null)
                    return DataDropChange<ShadowAppealOutcomeImportResult>.Refused($"{ServiceName} sent an empty answer.");
                return result.FileErrors.Count > 0
                    ? DataDropChange<ShadowAppealOutcomeImportResult>.Refused(result.FileErrors.ToArray())
                    : DataDropChange<ShadowAppealOutcomeImportResult>.Ok(result);
            }
            return DataDropChange<ShadowAppealOutcomeImportResult>.Refused($"{ServiceName} answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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
}

// ── models (claims-service shapes) ────────────────────────────────────────

public static class ShadowAppealAdvice
{
    public const string ChoWouldPay = "ChoWouldPay";
    public const string ChoWouldPayMore = "ChoWouldPayMore";
    public const string ChoDeniesForAnotherReason = "ChoDeniesForAnotherReason";
    public const string ChoConfirms = "ChoConfirms";
    public const string ChoUndecided = "ChoUndecided";
    public const string NotComparable = "NotComparable";

    public static string Label(string advice) => advice switch
    {
        ChoWouldPay => "CHO would pay",
        ChoWouldPayMore => "CHO would pay more",
        ChoDeniesForAnotherReason => "CHO denies, other reason",
        ChoConfirms => "CHO confirms",
        ChoUndecided => "CHO undecided",
        NotComparable => "Not decided by the core",
        _ => advice,
    };
}

public class ShadowAppealEvidence
{
    public string ClaimNumber { get; set; } = string.Empty;
    public string Advice { get; set; } = string.Empty;
    public string AdviceText { get; set; } = string.Empty;
    public bool LikelyOverturn { get; set; }
    public ShadowAppealResult Result { get; set; } = new();
    public ShadowAppealRow? DenialCodeHistory { get; set; }
}

/// <summary>The parts of a shadow result an appeal reviewer reads.</summary>
public class ShadowAppealResult
{
    public string RunId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string CoreOutcome { get; set; } = string.Empty;
    public string ChoOutcome { get; set; } = string.Empty;
    public decimal CorePaid { get; set; }
    public decimal ChoPaid { get; set; }
    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }
    public string? ChoPendCode { get; set; }
    public string? ChoPendReason { get; set; }
    public List<string> ChoStageFindings { get; set; } = new();
    public List<ShadowAppealLine> Lines { get; set; } = new();
    public List<ShadowAppealAuthorization> PriorAuthorizationEvidence { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}

public class ShadowAppealLine
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public decimal? CorePaid { get; set; }
    public decimal? ChoPaid { get; set; }
    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }
}

public class ShadowAppealAuthorization
{
    public int LineNumber { get; set; }
    public string? AuthorizationNumber { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? PolicyVersion { get; set; }
}

public class ShadowAppealRow
{
    public string Key { get; set; } = string.Empty;
    public int Decided { get; set; }
    public int Overturned { get; set; }
    public decimal? OverturnRate { get; set; }
    public int ChoWouldPay { get; set; }
    public int ChoWouldPayOverturned { get; set; }
    public int ChoConfirms { get; set; }
    public int ChoConfirmsOverturned { get; set; }
}

public class ShadowAppealSummary
{
    public DateTime AsOfUtc { get; set; }
    public int Decided { get; set; }
    public int Overturned { get; set; }
    public decimal? OverturnRate { get; set; }
    public int NotShadowed { get; set; }
    public List<ShadowAppealRow> ByAdvice { get; set; } = new();
    public List<ShadowAppealRow> ByDenialCode { get; set; } = new();
    public bool Truncated { get; set; }
}

public class ShadowAppealOutcomeImportResult
{
    public int Rows { get; set; }
    public int Loaded { get; set; }
    public int Rejected { get; set; }
    public bool DryRun { get; set; }
    public List<string> FileErrors { get; set; } = new();
    public List<string> RowErrors { get; set; } = new();
}
