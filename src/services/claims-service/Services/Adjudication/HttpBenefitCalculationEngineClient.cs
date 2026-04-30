using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.OperatingMode;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// HTTP adapter that satisfies <see cref="IBenefitCalculationEngine"/> from
/// claims-service by calling benefit-plan-service's
/// <c>POST /api/v1/adjudication/calculate-benefits</c> endpoint.
///
/// <para>
/// The benefit calculation engine ships as a class library (BP 5.10) but
/// its host-side collaborators (<see cref="IBenefitPlanProvider"/>,
/// <see cref="IAccumulatorService"/>, <see cref="IBenefitRuleGate"/>) are
/// wired in benefit-plan-service against benefit-plan-service's data
/// stores. Standing them up inside claims-service would mean importing
/// the entire plan + accumulator data layer — that's a Phase 2 split.
/// For 5.5 we proxy across the network so claims-service consumes the
/// canonical engine via the same HTTP surface portal/preview features
/// already use.
/// </para>
///
/// <para>
/// Only <see cref="CalculateAsync"/> is wired; the other interface
/// members (<see cref="CalculateWithModeAsync"/>,
/// <see cref="ReverseClaimAsync"/>) throw <see cref="NotImplementedException"/>
/// because the adjudication pipeline's <see cref="Stages.BenefitCalculationStage"/>
/// only uses CalculateAsync (Decision 13 — Replace mode only). Adding the
/// other surfaces is a follow-up driven by demand.
/// </para>
/// </summary>
public sealed class HttpBenefitCalculationEngineClient : IBenefitCalculationEngine
{
    public const string HttpClientName = "BenefitPlanService";
    private const string CalculatePath = "/api/v1/adjudication/calculate-benefits";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HttpBenefitCalculationEngineClient> _logger;

    public HttpBenefitCalculationEngineClient(
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HttpBenefitCalculationEngineClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<BenefitResolutionResult> CalculateAsync(
        BenefitResolutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, CalculatePath)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        var tenantId = ResolveTenantId();
        if (!string.IsNullOrEmpty(tenantId))
        {
            httpRequest.Headers.Add("X-Tenant-ID", tenantId);
        }

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Benefit-plan-service calculate-benefits returned {StatusCode} for claim {ClaimId}: {Body}",
                response.StatusCode, SanitizeForLog(request.ClaimId), SanitizeForLog(body));
            return new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "999",
                DenialReasonDescription =
                    $"Benefit calculation engine returned HTTP {(int)response.StatusCode}.",
            };
        }

        var result = await response.Content
            .ReadFromJsonAsync<BenefitResolutionResult>(JsonOptions, ct)
            .ConfigureAwait(false);

        return result ?? new BenefitResolutionResult
        {
            Success = false,
            DenialReasonCode = "999",
            DenialReasonDescription = "Benefit calculation engine returned an empty response.",
        };
    }

    public Task<AugmentResult<BenefitResolutionResult>> CalculateWithModeAsync(
        BenefitResolutionRequest request,
        IOperatingMode operatingMode,
        string tenantId,
        BenefitResolutionResult? legacyResult = null,
        CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "HttpBenefitCalculationEngineClient does not support operating-mode-aware " +
            "calculation. Augment-mode comparison ships in Phase 2 alongside legacy " +
            "result production.");
    }

    public Task ReverseClaimAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, DateOnly serviceDate,
        string originalClaimId,
        CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "HttpBenefitCalculationEngineClient does not support accumulator reversal. " +
            "Capability 5.12 (Adjustment Workflow) revisits the reversal surface.");
    }

    private string? ResolveTenantId()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return null;
        if (ctx.Items.TryGetValue("TenantId", out var value) && value is string s) return s;
        return null;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
