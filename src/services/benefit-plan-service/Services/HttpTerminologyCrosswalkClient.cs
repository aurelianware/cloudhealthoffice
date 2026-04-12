using System.Net.Http.Json;

namespace BenefitPlanService.Services;

/// <summary>
/// Calls the TerminologyService to resolve plan-specific code mappings.
/// Used as a pre-pricing step before the FeeScheduleEngine.
///
/// On failure, returns original codes unchanged — terminology crosswalk
/// is an enrichment step, not a gating step. A failed crosswalk means
/// the claim prices against the original code, which is the correct
/// fallback behavior.
/// </summary>
public class HttpTerminologyCrosswalkClient : ITerminologyCrosswalkClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpTerminologyCrosswalkClient> _logger;

    // Canonical system URIs matching TerminologyController constants
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string HcpcsSystem = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    private const string PlanCodeSystem = "http://cloudhealthoffice.com/plan-codes";

    public HttpTerminologyCrosswalkClient(
        HttpClient httpClient,
        ILogger<HttpTerminologyCrosswalkClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<CodeCrosswalkResult>> TranslateBatchAsync(
        string tenantId,
        List<CodeCrosswalkRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        try
        {
            var translateRequests = requests.Select(r => new TranslateRequestDto
            {
                System = r.CodeType == "CPT" ? CptSystem : HcpcsSystem,
                Code = r.ProcedureCode,
                TargetSystem = PlanCodeSystem,
                TenantId = tenantId
            }).ToList();

            var response = await _httpClient.PostAsJsonAsync(
                "fhir/ConceptMap/$batch-translate", translateRequests, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Terminology crosswalk returned {StatusCode} for {Count} codes; using originals",
                    response.StatusCode, requests.Count);
                return Passthrough(requests);
            }

            var translations = await response.Content
                .ReadFromJsonAsync<List<TranslateResponseDto>>(ct);

            if (translations is null || translations.Count != requests.Count)
                return Passthrough(requests);

            return requests.Zip(translations, (req, tx) =>
            {
                var match = tx.Matches?.FirstOrDefault();
                return new CodeCrosswalkResult
                {
                    LineNumber = req.LineNumber,
                    OriginalCode = req.ProcedureCode,
                    ResolvedCode = match?.Concept?.Code ?? req.ProcedureCode,
                    WasTranslated = tx.Result && match is not null,
                    MapVersionId = tx.MapVersionId
                };
            }).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Terminology crosswalk service unreachable; using original codes for {Count} lines",
                requests.Count);
            return Passthrough(requests);
        }
    }

    private static List<CodeCrosswalkResult> Passthrough(List<CodeCrosswalkRequest> requests)
        => requests.Select(r => new CodeCrosswalkResult
        {
            LineNumber = r.LineNumber,
            OriginalCode = r.ProcedureCode,
            ResolvedCode = r.ProcedureCode,
            WasTranslated = false
        }).ToList();

    /// <summary>
    /// Matches the TranslateRequest DTO expected by
    /// POST /fhir/ConceptMap/$batch-translate on TerminologyService.
    /// </summary>
    private record TranslateRequestDto
    {
        public string System { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string TargetSystem { get; init; } = string.Empty;
        public string? TenantId { get; init; }
    }

    private record TranslateResponseDto
    {
        public bool Result { get; init; }
        public List<MatchDto>? Matches { get; init; }
        public string? MapVersionId { get; init; }
    }

    private record MatchDto
    {
        public TranslatedCodingDto? Concept { get; init; }
    }

    private record TranslatedCodingDto
    {
        public string Code { get; init; } = string.Empty;
    }
}
