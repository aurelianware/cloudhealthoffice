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
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChoEligibilityAdapter> _logger;

    public string Platform => "cho";

    public ChoEligibilityAdapter(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ChoEligibilityAdapter> logger)
    {
        _httpClient = httpClient;
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
            LineOfBusiness = coverage.LineOfBusiness,
            Benefits = benefits,
            Deductible = accumulation.Deductible,
            OutOfPocket = accumulation.OutOfPocket,
            AdditionalInsurances = additionalInsurances
        };
    }

    private async Task<ChoCoverageDto?> GetActiveCoverageAsync(string tenantId, string subscriberId, DateTime serviceDate)
    {
        var coverageUrl = _configuration["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api";
        var response = await _httpClient.GetAsync(
            $"{coverageUrl}/coverage/member/{subscriberId}/active?serviceDate={serviceDate:yyyy-MM-dd}&tenantId={tenantId}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("No active coverage found for member {SubscriberId}", subscriberId);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ChoCoverageDto>();
    }

    private async Task<List<EligibilityBenefit>> GetBenefitsAsync(string tenantId, string benefitPlanId, string? serviceType)
    {
        var benefitUrl = _configuration["Services:BenefitPlanService"] ?? "http://benefit-plan-service.cloudhealthoffice/api";
        var url = $"{benefitUrl}/benefit-plans/{benefitPlanId}/benefits?tenantId={tenantId}";

        if (!string.IsNullOrEmpty(serviceType))
        {
            url += $"&serviceType={serviceType}";
        }

        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Benefits not found for plan {BenefitPlanId}", benefitPlanId);
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
            Percentage = b.Percentage,
            Quantity = b.Quantity,
            NetworkIndicator = b.NetworkIndicator,
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
            var benefitUrl = _configuration["Services:BenefitPlanService"] ?? "http://benefit-plan-service.cloudhealthoffice/api";
            var response = await _httpClient.GetAsync(
                $"{benefitUrl}/benefit-plans/{benefitPlanId}/accumulation/{subscriberId}?tenantId={tenantId}");

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
            var coverageUrl = _configuration["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api";
            var response = await _httpClient.GetAsync(
                $"{coverageUrl}/coverage/member/{subscriberId}/cob?tenantId={tenantId}");

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
}

/// <summary>
/// Extended coverage DTO that includes LineOfBusiness from the coverage service.
/// </summary>
internal class ChoCoverageDto
{
    public string Id { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string CoverageLevel { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string BenefitPlanId { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;
}
