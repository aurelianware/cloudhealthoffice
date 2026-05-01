using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="IBenefitPlanResolver"/> calling
/// benefit-plan-service's <c>GET /api/v1/plans/{id}</c>. Resolves a small
/// summary view of the plan that the adjudication pipeline needs;
/// benefit-plan-service remains the system-of-record for full plan
/// definitions.
///
/// <para>
/// Mirrors the existing <see cref="ClaimsService.EDI.Florida.HttpProviderService"/>
/// shape: <see cref="IHttpClientFactory"/> with a named client, a 5-second
/// timeout (configured at registration time), and non-throwing failure —
/// returns <c>null</c> on any error so the orchestrator can degrade
/// cleanly rather than crash the message handler.
/// </para>
/// </summary>
public class HttpBenefitPlanResolver : IBenefitPlanResolver
{
    public const string HttpClientName = "BenefitPlanService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpBenefitPlanResolver> _logger;

    public HttpBenefitPlanResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpBenefitPlanResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ResolvedBenefitPlan?> GetPlanAsync(
        string tenantId, string planId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/api/v1/plans/{Uri.EscapeDataString(planId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Benefit-plan-service returned {StatusCode} resolving plan {PlanId}",
                    response.StatusCode, SanitizeForLog(planId));
                return null;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<BenefitPlanDto>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (dto is null) return null;

            Guid? planGuid = Guid.TryParse(dto.Id, out var parsed) ? parsed : null;

            // Project the wire-shape NetworkTier[] down to the
            // pipeline-local view. Sort by TierLevel asc (1 = best) so the
            // enforcement stage can walk in priority order without
            // re-sorting on every claim.
            var tiers = (dto.NetworkTiers ?? new List<NetworkTierDto>())
                .OrderBy(t => t.TierLevel)
                .Select(t => new ResolvedNetworkTier
                {
                    TierName = t.TierName ?? string.Empty,
                    TierLevel = t.TierLevel,
                    NetworkId = string.IsNullOrWhiteSpace(t.NetworkId) ? null : t.NetworkId,
                })
                .ToList();

            return new ResolvedBenefitPlan
            {
                Id = dto.Id ?? planId,
                PlanGuid = planGuid,
                PlanName = dto.PlanName,
                PlanType = dto.PlanType,
                NetworkTiers = tiers,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "Failed to resolve benefit plan {PlanId} for tenant {TenantId}",
                SanitizeForLog(planId), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private sealed class BenefitPlanDto
    {
        public string? Id { get; set; }
        public string? PlanName { get; set; }
        public string? PlanType { get; set; }
        public List<NetworkTierDto>? NetworkTiers { get; set; }
    }

    private sealed class NetworkTierDto
    {
        public string? TierName { get; set; }
        public int TierLevel { get; set; }
        public string? NetworkId { get; set; }
    }
}
