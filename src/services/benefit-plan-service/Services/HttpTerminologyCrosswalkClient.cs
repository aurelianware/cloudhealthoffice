using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

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
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
        SlidingExpiration = TimeSpan.FromMinutes(10),
        Size = 1
    };

    // Short TTL for failure passthroughs so transient terminology-service
    // outages do not lock in untranslated codes for the full 30-minute window.
    private static readonly MemoryCacheEntryOptions FailureCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
        Size = 1
    };

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpTerminologyCrosswalkClient> _logger;

    // Canonical system URIs matching TerminologyController constants
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string HcpcsSystem = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    private const string PlanCodeSystem = "http://cloudhealthoffice.com/plan-codes";

    public HttpTerminologyCrosswalkClient(
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger<HttpTerminologyCrosswalkClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<CodeCrosswalkResult>> TranslateBatchAsync(
        string tenantId,
        List<CodeCrosswalkRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        var results = new CodeCrosswalkResult?[requests.Count];
        var misses = new List<(int Index, CodeCrosswalkRequest Request)>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (_cache.TryGetValue<CachedCrosswalkResult>(CacheKey(tenantId, request), out var cached)
                && cached is not null)
            {
                results[i] = ToResult(request, cached);
            }
            else
            {
                misses.Add((i, request));
            }
        }

        if (misses.Count == 0)
        {
            return results.Select(r => r!).ToList();
        }

        try
        {
            var translateRequests = misses.Select(m => new TranslateRequestDto
            {
                System = m.Request.CodeType == "CPT" ? CptSystem : HcpcsSystem,
                Code = m.Request.ProcedureCode,
                TargetSystem = PlanCodeSystem,
                TenantId = tenantId
            }).ToList();

            var response = await _httpClient.PostAsJsonAsync(
                "fhir/ConceptMap/$batch-translate", translateRequests, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Terminology crosswalk returned {StatusCode} for {Count} codes; using originals",
                    response.StatusCode, misses.Count);
                FillMissesWithPassthrough(tenantId, misses, results);
                return results.Select(r => r!).ToList();
            }

            var translations = await response.Content
                .ReadFromJsonAsync<List<TranslateResponseDto>>(ct);

            if (translations is null || translations.Count != misses.Count)
            {
                FillMissesWithPassthrough(tenantId, misses, results);
                return results.Select(r => r!).ToList();
            }

            foreach (var (miss, translation) in misses.Zip(translations))
            {
                var match = translation.Matches?.FirstOrDefault();
                var cached = new CachedCrosswalkResult(
                    match?.Concept?.Code ?? miss.Request.ProcedureCode,
                    translation.Result && match is not null,
                    translation.MapVersionId);

                _cache.Set(CacheKey(tenantId, miss.Request), cached, CacheOptions);
                results[miss.Index] = ToResult(miss.Request, cached);
            }

            return results.Select(r => r!).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Terminology crosswalk service unreachable; using original codes for {Count} lines",
                misses.Count);
            FillMissesWithPassthrough(tenantId, misses, results);
            return results.Select(r => r!).ToList();
        }
    }

    private void FillMissesWithPassthrough(
        string tenantId,
        List<(int Index, CodeCrosswalkRequest Request)> misses,
        CodeCrosswalkResult?[] results)
    {
        foreach (var (index, request) in misses)
        {
            var cached = new CachedCrosswalkResult(request.ProcedureCode, false, null);
            _cache.Set(CacheKey(tenantId, request), cached, FailureCacheOptions);
            results[index] = ToResult(request, cached);
        }
    }

    private static CodeCrosswalkResult ToResult(CodeCrosswalkRequest request, CachedCrosswalkResult cached)
        => new()
        {
            LineNumber = request.LineNumber,
            OriginalCode = request.ProcedureCode,
            ResolvedCode = cached.ResolvedCode,
            WasTranslated = cached.WasTranslated,
            MapVersionId = cached.MapVersionId
        };

    private static string CacheKey(string tenantId, CodeCrosswalkRequest request)
        => $"terminology-crosswalk:{tenantId}:{request.CodeType}:{request.ProcedureCode}";

    private sealed record CachedCrosswalkResult(
        string ResolvedCode,
        bool WasTranslated,
        string? MapVersionId);

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
