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
        var rebuild = await CalculateAccumulatorsWithClaimsAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct);
        return (rebuild.Success, rebuild.Snapshots);
    }

    /// <summary>
    /// The totals and each counted claim's contribution (claims-service
    /// <c>accumulator-totals</c> <c>claims</c>), so the Redis cache journals
    /// the rebuild per claim. An older claims-service sends no per-claim
    /// entries: the rebuild is then not journalled.
    /// </summary>
    public async Task<AccumulatorRebuildData> CalculateAccumulatorsWithClaimsAsync(
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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Names the tenant for ChoOutboundTokenHandler: an accumulator
            // rebuild outside a request still carries a service token for it.
            request.Headers.Add("X-Tenant-ID", tenantId);
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "claims-service unavailable during accumulator rebuild for owner {OwnerId}. " +
                "Returning empty snapshot — Redis cache will remain cold.",
                SanitizeForLog(ownerId));
            return new AccumulatorRebuildData(false, [], []);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "claims-service returned {Status} for accumulator-totals (owner={OwnerId}). " +
                "Returning empty snapshot.",
                (int)response.StatusCode, SanitizeForLog(ownerId));
            return new AccumulatorRebuildData(false, [], []);
        }

        var result = await response.Content.ReadFromJsonAsync<AccumulatorTotalsDto>(JsonOptions, ct);
        if (result?.Totals is null || result.Totals.Count == 0)
            return new AccumulatorRebuildData(true, [], []);

        var snapshots = result.Totals
            .Select(entry => MapToSnapshot(entry, scope))
            .Where(s => s is not null)
            .Cast<AccumulatorSnapshot>()
            .ToList();
        var claims = (result.Claims ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.ClaimId))
            .Select(c => new ClaimAccumulatorContribution(c.ClaimId, (c.Totals ?? [])
                .Select(entry => MapToSnapshot(entry, scope))
                .Where(s => s is not null)
                .Select(s => new AccumulatorUpdate
                {
                    Type = s!.Type, Scope = scope, NetworkTier = s.NetworkTier,
                    Amount = s.AccumulatedAmountAfter,
                    Source = s.Type is AccumulatorType.IndividualDeductible or AccumulatorType.FamilyDeductible ? "Deductible" : "OOP",
                })
                .ToList()))
            .Where(c => c.Updates.Count > 0)
            .ToList();
        return new AccumulatorRebuildData(true, snapshots, claims);
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
        public List<ClaimTotalsDto>? Claims { get; set; }
    }

    private sealed class ClaimTotalsDto
    {
        public string ClaimId { get; set; } = string.Empty;
        public List<AccumulatorTotalEntryDto>? Totals { get; set; }
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
