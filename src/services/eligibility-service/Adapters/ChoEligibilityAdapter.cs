using System.Net.Http.Json;
using EligibilityService.Models;
using EligibilityService.Services;

namespace EligibilityService.Adapters;

/// <summary>
/// Default eligibility adapter using CHO's internal microservices
/// (coverage-service, member-service, benefit-plan-service).
/// This preserves the existing eligibility verification behavior.
/// </summary>
public class ChoEligibilityAdapter : IEligibilityAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChoEligibilityAdapter> _logger;

    public string Platform => "cho";

    public ChoEligibilityAdapter(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChoEligibilityAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
        EligibilityAdapterRequest request, CancellationToken ct = default)
    {
        // 1. Check active coverage
        var coverage = await GetActiveCoverageAsync(request.TenantId, request.SubscriberId, request.ServiceDate);

        if (coverage == null || !coverage.IsActive)
        {
            return new EligibilityAdapterResponse
            {
                IsEligible = false,
                StatusCode = "6",
                RejectionReason = "No active coverage found for the service date"
            };
        }

        // 2. Get benefit plan details
        var benefits = await GetBenefitsAsync(request.TenantId, coverage.BenefitPlanId, request.ServiceTypeCode);

        // 3. Get accumulation (deductible/OOP)
        var accumulation = await GetAccumulationDataAsync(request.TenantId, request.SubscriberId, coverage.BenefitPlanId);

        // 4. Check COB
        var additionalInsurances = await GetAdditionalInsurancesAsync(request.TenantId, request.SubscriberId);

        return new EligibilityAdapterResponse
        {
            IsEligible = true,
            StatusCode = "1",
            CoverageLevel = coverage.CoverageLevel,
            PlanId = coverage.BenefitPlanId,
            PlanName = coverage.PlanName,
            GroupNumber = coverage.GroupNumber,
            CoverageBeginDate = coverage.EffectiveDate,
            CoverageEndDate = coverage.TerminationDate,
            LineOfBusiness = (LineOfBusiness)Math.Max(0, coverage.LineOfBusiness - 1),
            Benefits = benefits,
            Deductible = accumulation.Deductible,
            OutOfPocket = accumulation.OutOfPocket,
            AdditionalInsurances = additionalInsurances
        };
    }

    private async Task<ChoCoverageDto?> GetActiveCoverageAsync(string tenantId, string subscriberId, DateTime serviceDate)
    {
        var coverageUrl = _configuration["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api/v1";
        var client = _httpClientFactory.CreateClient("EligibilityDefault");
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{coverageUrl}/coverage/member/{subscriberId}/active?serviceDate={serviceDate:yyyy-MM-dd}&tenantId={tenantId}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("No active coverage found for member {SubscriberId}", SanitizeForLog(subscriberId));
            return null;
        }

        // The /active endpoint returns a List<Coverage> — take the first active entry
        var coverages = await response.Content.ReadFromJsonAsync<List<ChoCoverageDto>>();
        return coverages?.FirstOrDefault(c => c.IsActive);
    }

    private async Task<List<EligibilityBenefit>> GetBenefitsAsync(string tenantId, string benefitPlanId, string? serviceType)
    {
        var benefitUrl = _configuration["Services:BenefitPlanService"] ?? "http://benefit-plan-service.cloudhealthoffice/api/v1";
        var url = $"{benefitUrl}/plans/{benefitPlanId}/benefits?tenantId={tenantId}";

        if (!string.IsNullOrEmpty(serviceType))
        {
            url += $"&serviceType={serviceType}";
        }

        var client = _httpClientFactory.CreateClient("EligibilityDefault");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-ID", tenantId);
        var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Benefits not found for plan {BenefitPlanId}", SanitizeForLog(benefitPlanId));
            return new List<EligibilityBenefit>();
        }

        var benefitDtos = await response.Content.ReadFromJsonAsync<List<BenefitDto>>() ?? new List<BenefitDto>();

        return benefitDtos.Select(b => new EligibilityBenefit
        {
            ServiceTypeCode = b.ServiceTypeCode,
            ServiceTypeName = b.ServiceTypeName,
            CoverageLevel = b.CoverageLevel,
            InsuranceType = b.InsuranceType,
            TimePeriodQualifier = b.TimePeriodQualifier,
            MonetaryAmount = b.MonetaryAmount,
            // Benefit plan stores coinsurance as whole number (e.g. 20 for 20%);
            // portal renders with "P0" format which expects a fraction (0.20 → "20%")
            Percentage = b.Percentage.HasValue ? b.Percentage.Value / 100m : null,
            Quantity = b.Quantity,
            NetworkIndicator = string.IsNullOrEmpty(b.NetworkIndicator) ? "Y" : b.NetworkIndicator,
            AuthorizationRequired = b.AuthorizationRequired ? "Y" : "N",
            BenefitBeginDate = b.BenefitBeginDate,
            BenefitEndDate = b.BenefitEndDate
        }).ToList();
    }

    private async Task<(DeductibleInfo? Deductible, OutOfPocketInfo? OutOfPocket)> GetAccumulationDataAsync(
        string tenantId, string subscriberId, string benefitPlanId)
    {
        try
        {
            var benefitUrl = _configuration["Services:BenefitPlanService"] ?? "http://benefit-plan-service.cloudhealthoffice/api/v1";
            var client = _httpClientFactory.CreateClient("EligibilityDefault");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{benefitUrl}/plans/{benefitPlanId}/accumulation/{subscriberId}?tenantId={tenantId}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var accumulation = await response.Content.ReadFromJsonAsync<AccumulationDto>();
            return (accumulation?.Deductible, accumulation?.OutOfPocket);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting accumulation data");
            return (null, null);
        }
    }

    private async Task<List<AdditionalInsurance>> GetAdditionalInsurancesAsync(string tenantId, string subscriberId)
    {
        try
        {
            var coverageUrl = _configuration["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api/v1";
            var client = _httpClientFactory.CreateClient("EligibilityDefault");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{coverageUrl}/coverage/member/{subscriberId}/cob?tenantId={tenantId}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new List<AdditionalInsurance>();
            }

            var cobDtos = await response.Content.ReadFromJsonAsync<List<CobDto>>() ?? new List<CobDto>();

            return cobDtos.Select(c => new AdditionalInsurance
            {
                PayerName = c.PayerName,
                PayerId = c.PayerId,
                CoverageSequence = c.CoverageSequence,
                GroupNumber = c.GroupNumber,
                CoverageBeginDate = c.CoverageBeginDate,
                CoverageEndDate = c.CoverageEndDate
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting COB data");
            return new List<AdditionalInsurance>();
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// DTO matching the Coverage model returned by coverage-service.
/// Status is an int enum (1=Active, 2=Pending, 3=Terminated, 4=Suspended, 5=COBRA).
/// PlanId maps to BenefitPlanId in the eligibility context.
/// </summary>
internal class ChoCoverageDto
{
    public string Id { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? CoverageLevel { get; set; }
    public string? PlanName { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public int Status { get; set; }
    public int LineOfBusiness { get; set; } = 1;

    /// <summary>Coverage is active if Status == 1 (Active) or 5 (COBRA)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status is 1 or 5;

    /// <summary>Maps PlanId to BenefitPlanId for eligibility adapter</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BenefitPlanId => PlanId;
}
