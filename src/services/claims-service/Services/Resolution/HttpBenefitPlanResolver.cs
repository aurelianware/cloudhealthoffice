using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;

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
/// timeout (configured at registration time). A definitive 404 returns
/// <c>null</c> ("no such plan"); any other failure throws
/// <see cref="BenefitPlanUnavailableException"/> (#1299 S2) so the pipeline
/// can tell an outage from a missing plan.
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

            // Adjudication, including examiner approval re-runs, is a service
            // pipeline operation. Keep the caller tenant guard in the real
            // outbound handler; do not require downstream reads on user roles.
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
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
                    response.StatusCode, LogSanitizer.Sanitize(planId));
                throw new BenefitPlanUnavailableException(
                    $"benefit-plan-service returned {(int)response.StatusCode} for the plan");
            }

            var dto = await response.Content
                .ReadFromJsonAsync<BenefitPlanDto>(JsonOptions, ct)
                .ConfigureAwait(false)
                ?? throw new BenefitPlanUnavailableException("benefit-plan-service returned an empty plan body");

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

            // G-25: the plan's own prior-authorization flags. A benefit
            // flagged without CPT codes names a service category the
            // claims pipeline cannot map a line to, so it adds nothing here.
            var priorAuthCodes = new HashSet<string>(
                (dto.Benefits ?? new List<BenefitDto>())
                    .Where(b => b is not null && b.IsCovered && (b.PriorAuthRequired || b.RequiresPriorAuth))
                    .SelectMany(b => b.CptCodes ?? new List<string>())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return new ResolvedBenefitPlan
            {
                Id = dto.Id ?? planId,
                PlanGuid = planGuid,
                PlanCode = string.IsNullOrWhiteSpace(dto.PlanId) ? null : dto.PlanId.Trim(),
                PlanName = dto.PlanName,
                PlanType = dto.PlanType,
                NetworkTiers = tiers,
                PriorAuthRequiredProcedureCodes = priorAuthCodes,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not BenefitPlanUnavailableException)
        {
            // #1299 S2: not an answer about the plan (transport error,
            // HttpClient timeout, unreadable body). Reported as such rather
            // than as null, which means "no such plan" (a definitive 404).
            _logger.LogWarning(ex,
                "Failed to resolve benefit plan {PlanId} for tenant {TenantId}",
                LogSanitizer.Sanitize(planId), LogSanitizer.Sanitize(tenantId));
            throw new BenefitPlanUnavailableException(
                $"benefit plan lookup failed ({ex.GetType().Name})", ex);
        }
    }

    private sealed class BenefitPlanDto
    {
        public string? Id { get; set; }
        public string? PlanId { get; set; }
        public string? PlanName { get; set; }
        public string? PlanType { get; set; }
        public List<NetworkTierDto>? NetworkTiers { get; set; }
        public List<BenefitDto>? Benefits { get; set; }
    }

    /// <summary>The prior-authorization fields of <c>BenefitPlanService.Models.Benefit</c>.</summary>
    private sealed class BenefitDto
    {
        public bool IsCovered { get; set; } = true;
        public List<string>? CptCodes { get; set; }
        public bool PriorAuthRequired { get; set; }
        public bool RequiresPriorAuth { get; set; }
    }

    private sealed class NetworkTierDto
    {
        public string? TierName { get; set; }
        public int TierLevel { get; set; }
        public string? NetworkId { get; set; }
    }
}
