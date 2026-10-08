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
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, serviceDate, groupNumber: groupNumber);

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
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, serviceDate, serviceType);

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
        var coverage = await GetActiveCoverageAsync(tenantId, subscriberId, DateTime.Today, serviceTypeCode);

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

    /// <summary>
    /// The member's coverage in force on <paramref name="serviceDate"/> for this
    /// request. coverage-service's <c>/active</c> answers a list (every
    /// coverage in force that day: medical, dental, vision…, 404 when none);
    /// see <see cref="SelectCoverage"/> for which one answers the request.
    /// </summary>
    private async Task<CoverageDto?> GetActiveCoverageAsync(
        string tenantId, string subscriberId, DateTime serviceDate,
        string? serviceTypeCode = null, string? groupNumber = null)
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

            var coverages = await response.Content.ReadFromJsonAsync<List<CoverageDto>>();
            return SelectCoverage(coverages, serviceDate, serviceTypeCode, groupNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Coverage Service");
            throw;
        }
    }

    /// <summary>
    /// Picks the coverage that answers an eligibility request from
    /// coverage-service's <c>/active</c> list: in force on the date of service
    /// (span-based, <see cref="CoverageDto.IsInForceOn"/>), on the insurance
    /// line the service type belongs to (<see cref="InsuranceLineFor"/>; a
    /// coverage with no line is treated as health), preferring the requested
    /// group, then the most recent effective date. A dental-only member is not
    /// eligible for a medical service, so lines never substitute for each other.
    /// </summary>
    public static CoverageDto? SelectCoverage(
        IEnumerable<CoverageDto>? coverages, DateTime serviceDate, string? serviceTypeCode, string? groupNumber)
    {
        if (coverages is null) return null;

        var line = InsuranceLineFor(serviceTypeCode);
        var selected = coverages
            .Where(c => c.IsInForceOn(serviceDate))
            .Where(c => string.Equals(
                string.IsNullOrWhiteSpace(c.InsuranceLineCode) ? HealthLine : c.InsuranceLineCode.Trim(),
                line, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => !string.IsNullOrEmpty(groupNumber)
                                    && string.Equals(c.GroupNumber, groupNumber, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.EffectiveDate)
            .FirstOrDefault();

        if (selected != null) selected.IsActive = true;
        return selected;
    }

    private const string HealthLine = "HLT";

    // X12 EB/EQ service type codes that belong to the dental and vision lines;
    // everything else (including no service type, "30" health benefit plan
    // coverage) is a health (medical) question.
    private static readonly HashSet<string> DentalServiceTypes = new(StringComparer.OrdinalIgnoreCase)
        { "23", "24", "25", "26", "27", "28", "35", "36", "37", "38", "39", "40", "41" };
    private static readonly HashSet<string> VisionServiceTypes = new(StringComparer.OrdinalIgnoreCase)
        { "AL", "AM", "AO" };

    /// <summary>834 INS/HD insurance line code (HD03) a 270 service type is asked against.</summary>
    public static string InsuranceLineFor(string? serviceTypeCode)
    {
        if (string.IsNullOrWhiteSpace(serviceTypeCode)) return HealthLine;
        var code = serviceTypeCode.Trim();
        if (DentalServiceTypes.Contains(code)) return "DEN";
        if (VisionServiceTypes.Contains(code)) return "VIS";
        return HealthLine;
    }

    private async Task<MemberDto?> GetMemberAsync(string tenantId, string subscriberId)
    {
        try
        {
            var memberUrl = _configuration["Services:MemberService"] ?? "http://member-service.cloudhealthoffice/api";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{memberUrl}/members/{subscriberId}?tenantId={tenantId}");
            // Names the tenant for ChoOutboundTokenHandler, so a check with no
            // inbound caller carries a service token for it.
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await _httpClient.SendAsync(request);
            
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

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await _httpClient.SendAsync(request);
            
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
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{benefitUrl}/benefit-plans/{benefitPlanId}/accumulation/{subscriberId}?tenantId={tenantId}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            var response = await _httpClient.SendAsync(request);
            
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

/// <summary>
/// One entry of coverage-service's <c>GET /coverage/member/{id}/active</c>
/// list — its <c>Coverage</c> document as serialized there: camelCase
/// properties, enums by name.
/// </summary>
public class CoverageDto
{
    public string Id { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>coverage-service's <c>planId</c> (the benefit plan id).</summary>
    [JsonPropertyName("planId")]
    public string BenefitPlanId { get; set; } = string.Empty;

    /// <summary>834 HD03 insurance line (HLT, DEN, VIS…); null on older records.</summary>
    public string? InsuranceLineCode { get; set; }

    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    /// <summary>coverage-service CoverageStatus (1=Active … 5=COBRA), sent by name.</summary>
    [JsonConverter(typeof(CoverageStatusIntConverter))]
    public int Status { get; set; }

    /// <summary>
    /// Set when this coverage was selected as in force for the request
    /// (<see cref="EligibilityServiceImpl.SelectCoverage"/>); not on the wire.
    /// </summary>
    [JsonIgnore]
    public bool IsActive { get; set; }

    /// <summary>
    /// In force on the date of service: within the effective/termination span,
    /// with a status that is in force for that span (Active; Pending, which is
    /// only the auto-assigned "not yet effective" state; Terminated with a
    /// termination date; COBRA). Mirrors coverage-service <c>Coverage.IsActiveOn</c>
    /// / <c>DateOfServiceStatuses</c>.
    /// </summary>
    public bool IsInForceOn(DateTime serviceDate)
    {
        var date = serviceDate.Date;
        if (Status is not (1 or 2 or 3 or 5)) return false;
        if (Status == 3 && !TerminationDate.HasValue) return false;
        return date >= EffectiveDate.Date
            && (!TerminationDate.HasValue || date <= TerminationDate.Value.Date);
    }
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
