using EligibilityService.Adapters;
using EligibilityService.Models;
using EligibilityService.Repositories;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EligibilityService.Services;

public class EligibilityServiceImpl : IEligibilityService
{
    private readonly IEligibilityRepository _repository;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EligibilityServiceImpl> _logger;
    private readonly IConfiguration _configuration;
    private readonly EligibilityAdapterFactory _adapterFactory;

    public EligibilityServiceImpl(
        IEligibilityRepository repository,
        HttpClient httpClient,
        ILogger<EligibilityServiceImpl> logger,
        IConfiguration configuration,
        EligibilityAdapterFactory adapterFactory)
    {
        _repository = repository;
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _adapterFactory = adapterFactory;
    }

    public async Task<EligibilityResponse> ProcessInquiryAsync(EligibilityInquiry inquiry)
    {
        inquiry.Status = EligibilityInquiryStatus.Processing;
        inquiry.CreatedDate = DateTime.UtcNow;

        // Store inquiry
        await _repository.CreateInquiryAsync(inquiry);

        try
        {
            // Resolve the eligibility adapter for this tenant
            var (adapter, platformSettings) = await _adapterFactory.GetAdapterWithSettingsAsync(inquiry.TenantId);

            _logger.LogInformation(
                "Processing eligibility inquiry {InquiryId} using {Platform} adapter for tenant {TenantId}",
                SanitizeForLog(inquiry.Id), adapter.Platform, SanitizeForLog(inquiry.TenantId));

            var adapterRequest = new EligibilityAdapterRequest
            {
                TenantId = inquiry.TenantId,
                SubscriberId = inquiry.SubscriberId,
                GroupNumber = inquiry.GroupNumber,
                ProviderNPI = inquiry.ProviderNPI,
                ServiceTypeCode = inquiry.ServiceTypeCode,
                ServiceDate = inquiry.ServiceDateFrom ?? DateTime.UtcNow,
                ServiceDateTo = inquiry.ServiceDateTo,
                SubscriberFirstName = inquiry.SubscriberFirstName,
                SubscriberLastName = inquiry.SubscriberLastName,
                SubscriberDOB = inquiry.SubscriberDOB,
                DependentFirstName = inquiry.DependentFirstName,
                DependentLastName = inquiry.DependentLastName,
                DependentDOB = inquiry.DependentDOB,
                DependentRelationship = inquiry.DependentRelationship,
                PayerId = inquiry.PayerId,
                PayerName = inquiry.PayerName,
                PlatformSettings = platformSettings
            };

            var adapterResponse = await adapter.VerifyEligibilityAsync(adapterRequest);

            // Map adapter response to EligibilityResponse
            var response = new EligibilityResponse
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = inquiry.TenantId,
                InquiryId = inquiry.Id,
                ControlNumber = inquiry.ControlNumber,
                ResponseCode = adapterResponse.IsEligible ? "Y" : "N",
                StatusCode = adapterResponse.StatusCode,
                RejectionReason = adapterResponse.RejectionReason,
                IsCovered = adapterResponse.IsEligible,
                CoverageLevel = adapterResponse.CoverageLevel ?? string.Empty,
                InsurancePlanName = adapterResponse.PlanName ?? string.Empty,
                GroupNumber = adapterResponse.GroupNumber ?? string.Empty,
                CoverageBeginDate = adapterResponse.CoverageBeginDate,
                CoverageEndDate = adapterResponse.CoverageEndDate,
                Benefits = adapterResponse.Benefits,
                Deductible = adapterResponse.Deductible,
                OutOfPocket = adapterResponse.OutOfPocket,
                AdditionalInsurances = adapterResponse.AdditionalInsurances,
                CreatedDate = DateTime.UtcNow
            };

            // Update inquiry status
            inquiry.Status = EligibilityInquiryStatus.Completed;
            inquiry.ResponseId = response.Id;
            inquiry.CompletedDate = DateTime.UtcNow;
            await _repository.UpdateInquiryAsync(inquiry);

            // Store response
            await _repository.CreateResponseAsync(response);

            _logger.LogInformation("Eligibility inquiry {InquiryId} completed successfully", SanitizeForLog(inquiry.Id));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing eligibility inquiry {InquiryId}", SanitizeForLog(inquiry.Id));

            inquiry.Status = EligibilityInquiryStatus.Failed;
            inquiry.CompletedDate = DateTime.UtcNow;
            await _repository.UpdateInquiryAsync(inquiry);

            throw;
        }
    }

    public async Task<(bool IsActive, string StatusCode, string CoverageLevel, string Message)> QuickEligibilityCheckAsync(
        string tenantId, string subscriberId, string? groupNumber, DateTime serviceDate)
    {
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, serviceDate);
        
        if (coverage == null)
        {
            return (false, "6", "", "No coverage found");
        }

        if (!coverage.IsActive)
        {
            return (false, "6", coverage.CoverageLevel, "Coverage inactive");
        }

        return (true, "1", coverage.CoverageLevel, "Active coverage");
    }

    public async Task<List<EligibilityBenefit>> GetBenefitDetailsAsync(
        string tenantId, string subscriberId, string? serviceType, DateTime serviceDate)
    {
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, serviceDate);
        
        if (coverage == null)
        {
            return new List<EligibilityBenefit>();
        }

        return await GetBenefitsAsync(tenantId, coverage.BenefitPlanId, serviceType);
    }

    public async Task<(DeductibleInfo? Deductible, OutOfPocketInfo? OutOfPocket)> GetAccumulationAsync(
        string tenantId, string subscriberId)
    {
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, DateTime.Today);
        
        if (coverage == null)
        {
            return (null, null);
        }

        return await GetAccumulationDataAsync(tenantId, subscriberId, coverage.BenefitPlanId);
    }

    public async Task<List<EligibilityInquiry>> GetInquiryHistoryAsync(
        string tenantId, string subscriberId, int page, int pageSize)
    {
        return await _repository.GetInquiriesBySubscriberAsync(tenantId, subscriberId, page, pageSize);
    }

    public async Task<(bool Required, string Reason)> CheckAuthRequirementAsync(
        string tenantId, string subscriberId, string serviceTypeCode, string? procedureCode)
    {
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, DateTime.Today);
        
        if (coverage == null)
        {
            return (false, "No active coverage");
        }

        var benefits = await GetBenefitsAsync(tenantId, coverage.BenefitPlanId, serviceTypeCode);
        var benefit = benefits.FirstOrDefault(b => b.ServiceTypeCode == serviceTypeCode);

        if (benefit?.AuthorizationRequired == "Y")
        {
            return (true, $"Prior authorization required for {benefit.ServiceTypeName}");
        }

        return (false, "No authorization required");
    }

    // Private helper methods

    private async Task<CoverageDto?> GetActiveCoverageAsync(string tenantId, string subscriberId, DateTime serviceDate)
    {
        try
        {
            var coverageUrl = _configuration["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api/v1";
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{coverageUrl}/coverage/member/{subscriberId}/active?serviceDate={serviceDate:yyyy-MM-dd}&tenantId={tenantId}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("No active coverage found for member {SubscriberId}", SanitizeForLog(subscriberId));
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CoverageDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Coverage Service");
            throw;
        }
    }

    private async Task<MemberDto?> GetMemberAsync(string tenantId, string subscriberId)
    {
        try
        {
            var memberUrl = _configuration["Services:MemberService"] ?? "http://member-service.cloudhealthoffice/api";
            var response = await _httpClient.GetAsync($"{memberUrl}/members/{subscriberId}?tenantId={tenantId}");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Member {SubscriberId} not found", subscriberId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MemberDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Member Service");
            throw;
        }
    }

    private async Task<List<EligibilityBenefit>> GetBenefitsAsync(string tenantId, string benefitPlanId, string? serviceType)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Benefit Plan Service");
            throw;
        }
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
                _logger.LogWarning("Accumulation not found for member {SubscriberId}", SanitizeForLog(subscriberId));
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
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{coverageUrl}/coverage/member/{subscriberId}/cob?tenantId={tenantId}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await _httpClient.SendAsync(request);
            
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

    private EligibilityResponse CreateInactiveCoverageResponse(EligibilityInquiry inquiry)
    {
        return new EligibilityResponse
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = inquiry.TenantId,
            InquiryId = inquiry.Id,
            ControlNumber = inquiry.ControlNumber,
            ResponseCode = "N", // No - no active coverage
            StatusCode = "6", // Inactive
            RejectionReason = "No active coverage found for the service date",
            IsCovered = false,
            CoverageLevel = string.Empty,
            CreatedDate = DateTime.UtcNow
        };
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove newline characters to prevent log forging via user-controlled data.
        return value.Replace("\r", string.Empty)
                    .Replace("\n", string.Empty);
    }
}

// DTOs for service calls
public class CoverageDto
{
    public string Id { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string CoverageLevel { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string BenefitPlanId { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class MemberDto
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string Gender { get; set; } = string.Empty;
}

/// <summary>
/// DTO for deserialising the benefit-plan-service GET /plans/{id}/benefits response.
/// JsonPropertyName attributes map from the benefit-plan-service Benefit model field names
/// to the eligibility-domain field names used by the adapter.
/// </summary>
public class BenefitDto
{
    [JsonPropertyName("serviceCategory")]
    public string ServiceTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string ServiceTypeName { get; set; } = string.Empty;

    public string CoverageLevel { get; set; } = string.Empty;
    public string InsuranceType { get; set; } = string.Empty;
    public string TimePeriodQualifier { get; set; } = string.Empty;

    [JsonPropertyName("inNetworkCopay")]
    public decimal? MonetaryAmount { get; set; }

    [JsonPropertyName("inNetworkCoinsurance")]
    public decimal? Percentage { get; set; }

    [JsonPropertyName("visitLimit")]
    public int? Quantity { get; set; }

    public string NetworkIndicator { get; set; } = string.Empty;

    [JsonPropertyName("priorAuthRequired")]
    public bool AuthorizationRequired { get; set; }

    public DateTime? BenefitBeginDate { get; set; }
    public DateTime? BenefitEndDate { get; set; }
}

public class AccumulationDto
{
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }
}

public class CobDto
{
    public string PayerName { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string CoverageSequence { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public DateTime CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
}
