using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// 5.5 wired <see cref="CalculateAsync"/>. Capability 5.12a wires
/// <see cref="ReverseClaimAsync"/> through to BP service's new
/// <c>POST /api/v1/adjudication/reverse-claim</c> endpoint (Gap D15
/// ratification — engine-side <c>BenefitCalculationEngine.ReverseClaimAsync</c>
/// has been wired through <c>ChoAccumulatorService.ReverseAsync</c> with
/// <c>IsReversed=true</c> journaling since BP 5.10; only the HTTP
/// surface was missing). <see cref="CalculateWithModeAsync"/> remains
/// deferred to Phase 2.
/// </para>
/// </summary>
public sealed class HttpBenefitCalculationEngineClient : IBenefitCalculationEngine
{
    public const string HttpClientName = "BenefitPlanService";
    private const string CalculatePath = "/api/v1/adjudication/calculate-benefits";
    private const string ReversePath = "/api/v1/adjudication/reverse-claim";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAdjudicationTenantContext _tenantContext;
    private readonly ILogger<HttpBenefitCalculationEngineClient> _logger;

    public HttpBenefitCalculationEngineClient(
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        IAdjudicationTenantContext tenantContext,
        ILogger<HttpBenefitCalculationEngineClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _tenantContext = tenantContext;
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

    public async Task ReverseClaimAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, DateOnly serviceDate,
        string originalClaimId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            throw new ArgumentException("memberId is required", nameof(memberId));
        if (string.IsNullOrWhiteSpace(originalClaimId))
            throw new ArgumentException("originalClaimId is required", nameof(originalClaimId));
        if (benefitPlanId == Guid.Empty)
            throw new ArgumentException("benefitPlanId is required", nameof(benefitPlanId));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var payload = new ReverseClaimRequest
        {
            MemberId = memberId,
            SubscriberId = subscriberId,
            BenefitPlanId = benefitPlanId,
            ServiceDate = serviceDate,
            OriginalClaimId = originalClaimId,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ReversePath)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
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
            _logger.LogError(
                "Benefit-plan-service reverse-claim returned {StatusCode} for original claim {OriginalClaimId}: {Body}",
                response.StatusCode, SanitizeForLog(originalClaimId), SanitizeForLog(body));
            // The engine contract is "throws on failure" — caller (5.12b
            // ReversalRunService) catches and surfaces as ClaimAdjustment
            // failure with manual triage required.
            throw new HttpRequestException(
                $"Benefit calculation engine reversal returned HTTP {(int)response.StatusCode} " +
                $"for original claim {originalClaimId}");
        }

        _logger.LogInformation(
            "Reversed accumulator impact for original claim {OriginalClaimId} (member {MemberId}, plan {PlanId})",
            SanitizeForLog(originalClaimId), SanitizeForLog(memberId), benefitPlanId);
    }

    /// <summary>Wire payload for BP <c>POST /api/v1/adjudication/reverse-claim</c>.</summary>
    private sealed class ReverseClaimRequest
    {
        public string MemberId { get; set; } = string.Empty;
        public string SubscriberId { get; set; } = string.Empty;
        public Guid BenefitPlanId { get; set; }
        public DateOnly ServiceDate { get; set; }
        public string OriginalClaimId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tenant id sourced from the scoped <see cref="IAdjudicationTenantContext"/>
    /// first (set by the orchestrator before each pipeline run from a
    /// background subscription) and falling back to the inbound HTTP
    /// request's resolved tenant. Either route ensures benefit-plan-service
    /// receives the <c>X-Tenant-ID</c> header it requires regardless of
    /// whether the engine is invoked from the orchestrator or from a
    /// future synchronous controller path.
    /// </summary>
    private string? ResolveTenantId()
    {
        if (!string.IsNullOrEmpty(_tenantContext.TenantId)) return _tenantContext.TenantId;

        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return null;
        if (ctx.Items.TryGetValue("TenantId", out var value) && value is string s) return s;
        return null;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
