using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace BenefitPlanService.Services;

/// <summary>
/// Calls provider-verification-service to check provider integrity.
/// Results are cached for 1 hour since exclusion list data changes infrequently
/// (OIG publishes monthly, SAM.gov daily but latency is acceptable for adjudication).
///
/// On service failure, defaults to Passed=true so adjudication is not blocked.
/// The separate provider-verification-service handles scheduled re-verification.
/// </summary>
public class HttpProviderIntegrityGate : IProviderIntegrityGate
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpProviderIntegrityGate> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public HttpProviderIntegrityGate(
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger<HttpProviderIntegrityGate> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ProviderIntegrityResult> CheckAsync(
        string npi,
        CancellationToken ct = default)
    {
        var cacheKey = $"provider-integrity:{npi}";

        if (_cache.TryGetValue<ProviderIntegrityResult>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var response = await _httpClient.GetAsync(
                $"api/providers/{npi}/verification", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider verification service returned {StatusCode} for NPI {Npi}; passing through",
                    response.StatusCode, npi);
                return Passthrough();
            }

            var record = await response.Content.ReadFromJsonAsync<VerificationResponse>(ct);

            if (record is null) return Passthrough();

            var result = new ProviderIntegrityResult
            {
                Passed = record.Status is not ("Excluded" or "Failed"),
                IntegrityScore = record.IntegrityScore,
                Rating = record.Rating,
                IsExcluded = record.IsExcluded,
                DenialCode = record.IsExcluded ? "B7" : null,
                DenialReason = record.IsExcluded
                    ? $"Provider NPI {npi} is excluded from federal healthcare programs"
                    : null
            };

            _cache.Set(cacheKey, result, CacheTtl);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider verification service unreachable for NPI {Npi}; passing through", npi);
            return Passthrough();
        }
    }

    private static ProviderIntegrityResult Passthrough() => new()
    {
        Passed = true,
        Rating = "Unknown"
    };

    private record VerificationResponse
    {
        public string Status { get; init; } = string.Empty;
        public int IntegrityScore { get; init; }
        public string? Rating { get; init; }
        public bool IsExcluded { get; init; }
    }
}
