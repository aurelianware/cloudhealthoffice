using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging;

namespace BenefitPlanService.Services;

/// <summary>
/// Implements <see cref="IClaimsAccumulatorSource"/> by calling the claims-service
/// <c>GET /api/claims/accumulator-totals</c> endpoint.
///
/// This is the "source of truth" path for the Redis accumulator cache:
///   RedisAccumulatorService.GetOrRebuildAsync → IClaimsAccumulatorSource → claims-service
///
/// The typed HttpClient (<c>ClaimsServiceClient</c>) is registered in Program.cs with
/// the base address read from <c>Services:ClaimsServiceUrl</c>.
/// </summary>
public class ClaimsServiceAccumulatorSource : IClaimsAccumulatorSource
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaimsServiceAccumulatorSource> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ClaimsServiceAccumulatorSource(
        HttpClient http,
        ILogger<ClaimsServiceAccumulatorSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<(bool Success, IReadOnlyList<AccumulatorSnapshot> Snapshots)> CalculateAccumulatorsAsync(
        string tenantId,
        string ownerId,
        AccumulatorScope scope,
        Guid benefitPlanId,
        string planYear,
        CancellationToken ct = default)
    {
        var scopeStr = scope == AccumulatorScope.Family ? "Family" : "Individual";

        var url = $"api/claims/accumulator-totals" +
                  $"?ownerId={Uri.EscapeDataString(ownerId)}" +
                  $"&scope={scopeStr}" +
                  $"&benefitPlanId={Uri.EscapeDataString(benefitPlanId.ToString())}" +
                  $"&planYear={Uri.EscapeDataString(planYear)}";

        _logger.LogDebug(
            "Fetching accumulator totals from claims-service: owner={OwnerId}, scope={Scope}, plan={PlanId}, year={Year}",
            SanitizeForLog(ownerId), scopeStr, benefitPlanId, planYear);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "claims-service unavailable during accumulator rebuild for owner {OwnerId}. " +
                "Returning empty snapshot — Redis cache will remain cold.",
                SanitizeForLog(ownerId));
            return (false, Array.Empty<AccumulatorSnapshot>());
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "claims-service returned {Status} for accumulator-totals (owner={OwnerId}). " +
                "Returning empty snapshot.",
                (int)response.StatusCode, SanitizeForLog(ownerId));
            return (false, Array.Empty<AccumulatorSnapshot>());
        }

        var result = await response.Content.ReadFromJsonAsync<AccumulatorTotalsDto>(JsonOptions, ct);
        if (result?.Totals is null || result.Totals.Count == 0)
            return (true, Array.Empty<AccumulatorSnapshot>());

        return (true, result.Totals
            .Select(entry => MapToSnapshot(entry, scope))
            .Where(s => s is not null)
            .Cast<AccumulatorSnapshot>()
            .ToList());
    }

    private static AccumulatorSnapshot? MapToSnapshot(AccumulatorTotalEntryDto entry, AccumulatorScope scope)
    {
        // Only map the money accumulators the benefit engine tracks.
        // Coinsurance and Copay are sub-components of OOP max — they are stored in
        // the claims-service breakdown but are NOT separate Redis accumulator buckets;
        // they are already included in the IndividualOutOfPocketMax / FamilyOutOfPocketMax total.
        if (!Enum.TryParse<AccumulatorType>(entry.AccumulatorType, out var type))
            return null;

        if (!Enum.TryParse<NetworkTier>(entry.NetworkTier, out var tier))
            tier = NetworkTier.InNetwork;

        return new AccumulatorSnapshot
        {
            Type = type,
            Scope = scope,
            NetworkTier = tier,
            LimitAmount = 0, // Limits come from BenefitPlanConfig, not claim history
            AccumulatedAmountAfter = entry.AccumulatedAmount
        };
    }

    // ── Local DTOs (mirror claims-service response; avoids a cross-project reference) ──

    private sealed class AccumulatorTotalsDto
    {
        public List<AccumulatorTotalEntryDto> Totals { get; set; } = new();
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private sealed class AccumulatorTotalEntryDto
    {
        public string AccumulatorType { get; set; } = string.Empty;
        public string NetworkTier { get; set; } = string.Empty;
        public decimal AccumulatedAmount { get; set; }
    }
}
