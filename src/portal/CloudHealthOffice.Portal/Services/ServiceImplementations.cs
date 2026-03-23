using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net;
using Microsoft.Identity.Web;
using MongoDB.Driver;
using MongoDB.Bson;

namespace CloudHealthOffice.Portal.Services;

public class ClaimsService : IClaimsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimsService> _logger;

    public ClaimsService(HttpClient httpClient, IConfiguration configuration, ILogger<ClaimsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ClaimSummary>> GetRecentClaimsAsync(int count)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var claims = await _httpClient.GetFromJsonAsync<List<ClaimSummary>>($"{baseUrl}/claims/recent?count={count}");
            return claims ?? new List<ClaimSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimDetails?> GetClaimByIdAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ClaimDetails>($"{baseUrl}/claims/{claimId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<string> SubmitClaimAsync(SubmitClaimRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/claims", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitClaimResponse>();
            return result?.ClaimId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/claims/search", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ClaimSearchResult>();
            return result ?? new ClaimSearchResult();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task UpdateClaimStatusAsync(string claimId, string status, string? notes = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var updateRequest = new { status, notes };
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/claims/{claimId}/status", updateRequest);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AdjudicationTransparencyData>($"{baseUrl}/claims/{claimId}/adjudication-detail");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private class SubmitClaimResponse
    {
        public string ClaimId { get; set; } = string.Empty;
    }
}

public class EligibilityService : IEligibilityService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EligibilityService> _logger;

    public EligibilityService(HttpClient httpClient, IConfiguration configuration, ILogger<EligibilityService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EligibilityResponse> CheckEligibilityAsync(object request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/eligibility/inquiry", request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EligibilityResponse>()
                ?? throw new Exception("No response from eligibility service");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Eligibility Service");
            throw new ServiceUnavailableException("Eligibility Service", ex);
        }
    }
}

public class MemberService : IMemberService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberService> _logger;

    public MemberService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberSummary>> SearchMembersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var members = await _httpClient.GetFromJsonAsync<List<MemberSummary>>($"{baseUrl}/members/search?q={searchTerm}");
            return members ?? new List<MemberSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberDetails?> GetMemberByIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberDetails>($"{baseUrl}/members/{memberId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberPcp?> GetMemberPcpAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberPcp>($"{baseUrl}/members/{memberId}/pcp");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task AssignPcpAsync(AssignPcpRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/members/{request.MemberId}/pcp", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var history = await _httpClient.GetFromJsonAsync<List<CoverageHistoryEvent>>($"{baseUrl}/members/{memberId}/coverage-history");
            return history ?? new List<CoverageHistoryEvent>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/members/{memberId}/834-transactions");
            return records ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/members/{request.MemberId}/terminate", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAccumulators> GetAccumulatorsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var accums = await _httpClient.GetFromJsonAsync<MemberAccumulators>($"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/accumulators");
            return accums ?? new MemberAccumulators();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}

public class CoverageService : ICoverageService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CoverageService> _logger;

    public CoverageService(HttpClient httpClient, IConfiguration configuration, ILogger<CoverageService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:CoverageService"];
        try
        {
            var coverage = await _httpClient.GetFromJsonAsync<List<Coverage>>($"{baseUrl}/coverage/member/{memberId}");
            return coverage ?? new List<Coverage>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Coverage Service");
            throw new ServiceUnavailableException("Coverage Service", ex);
        }
    }
}

public class AuthorizationService : IAuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthorizationService> _logger;
    private readonly ITokenAcquisition _tokenAcquisition;

    public AuthorizationService(HttpClient httpClient, IConfiguration configuration, ILogger<AuthorizationService> logger, ITokenAcquisition tokenAcquisition)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
    }

    public async Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            var url = string.IsNullOrEmpty(memberId)
                ? $"{baseUrl}/authorizations"
                : $"{baseUrl}/authorizations?memberId={memberId}";
            var auths = await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>(url);
            return auths ?? new List<AuthorizationSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            return await _httpClient.GetFromJsonAsync<AuthorizationDetails>($"{baseUrl}/authorizations/{authorizationId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/authorizations", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitAuthorizationResponse>();
            return result?.AuthorizationId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    private async Task SetBearerTokenAsync()
    {
        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Authorization.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private class SubmitAuthorizationResponse
    {
        public string AuthorizationId { get; set; } = string.Empty;
    }
}

public class ProviderService : IProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderService> _logger;

    public ProviderService(HttpClient httpClient, IConfiguration configuration, ILogger<ProviderService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var providers = await _httpClient.GetFromJsonAsync<List<ProviderSummary>>($"{baseUrl}/providers/search?q={searchTerm}");
            return providers ?? new List<ProviderSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<ProviderListItem>> SearchProvidersAsync(string? specialty = null, string? networkStatus = null, string? searchTerm = null)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var query = $"{baseUrl}/providers/list?";
            if (!string.IsNullOrEmpty(specialty))
                query += $"specialty={specialty}&";
            if (!string.IsNullOrEmpty(networkStatus))
                query += $"networkStatus={networkStatus}&";
            if (!string.IsNullOrEmpty(searchTerm))
                query += $"search={searchTerm}";

            var providers = await _httpClient.GetFromJsonAsync<List<ProviderListItem>>(query);
            return providers ?? new List<ProviderListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderDetails?> GetProviderByIdAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ProviderDetails>($"{baseUrl}/providers/{providerId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<string> CreateProviderAsync(CreateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/providers", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<dynamic>();
            return result?.providerId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task UpdateProviderAsync(string providerId, UpdateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/providers/{providerId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<string>> GetSpecialtiesAsync()
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var specialties = await _httpClient.GetFromJsonAsync<List<string>>($"{baseUrl}/providers/specialties");
            return specialties ?? new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }
}


public class BenefitPlanService : IBenefitPlanService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BenefitPlanService> _logger;

    public BenefitPlanService(HttpClient httpClient, IConfiguration configuration, ILogger<BenefitPlanService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<BenefitPlan>> GetBenefitPlansAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlan>>($"{baseUrl}/benefit-plans");
            return plans ?? new List<BenefitPlan>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var query = $"{baseUrl}/benefit-plans/search?";
            if (!string.IsNullOrEmpty(sponsorId))
                query += $"sponsorId={sponsorId}&";
            if (!string.IsNullOrEmpty(productType))
                query += $"productType={productType}";

            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlanListItem>>(query);
            return plans ?? new List<BenefitPlanListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<BenefitPlanDetails>($"{baseUrl}/benefit-plans/{planId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/benefit-plans", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateBenefitPlanResponse>();
            return result?.PlanId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/benefit-plans/{planId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<BenefitItem>> GetAvailableBenefitsAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var benefits = await _httpClient.GetFromJsonAsync<List<BenefitItem>>($"{baseUrl}/benefits");
            return benefits ?? new List<BenefitItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<ServiceBenefitRule>> GetServiceBenefitRulesAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ServiceBenefitRule>>($"{baseUrl}/benefit-plans/{planId}/service-rules");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateServiceBenefitRulesAsync(UpdateServiceBenefitRulesRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/benefit-plans/{request.PlanId}/service-rules", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<AccumulatorConfiguration?> GetAccumulatorConfigAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AccumulatorConfiguration>($"{baseUrl}/benefit-plans/{planId}/accumulators");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateAccumulatorConfigAsync(string planId, AccumulatorConfiguration config)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/benefit-plans/{planId}/accumulators", config);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    private class CreateBenefitPlanResponse
    {
        public string PlanId { get; set; } = string.Empty;
    }
}

public class WorkflowService : IWorkflowService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public WorkflowService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?limit={limit}");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            return await _httpClient.GetFromJsonAsync<WorkflowDetails>($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<List<WorkflowRun>> GetActiveWorkflowsAsync()
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?phase=Running");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<bool> RetriggerWorkflowAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }
}

// TEMPORARY: Replace when Prometheus is deployed.
// MetricsService retains mock data until real Prometheus metrics are wired up.
public class MetricsService : IMetricsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(HttpClient httpClient, IConfiguration configuration, ILogger<MetricsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        // TODO: Query Prometheus for real metrics
        return new DashboardMetrics
        {
            TotalClaims = 2847,
            ClaimsTrend = 0.042,
            ApprovalRate = 0.962,
            AvgProcessingTimeMs = 340,
            TotalPayerAmount = 1_847_293.00m,
            ApprovedClaims = 2738,
            DeniedClaims = 57,
            PendingClaims = 52
        };
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<OperationalAlerts> GetOperationalAlertsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var alerts = await _httpClient.GetFromJsonAsync<OperationalAlerts>($"{baseUrl}/metrics/operational-alerts");
            return alerts ?? GetDefaultAlerts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching operational alerts, returning defaults");
            return GetDefaultAlerts();
        }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<EdiVolumeSummary> GetTodayEdiVolumeAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var volume = await _httpClient.GetFromJsonAsync<EdiVolumeSummary>($"{baseUrl}/metrics/edi-volume/today");
            return volume ?? GetDefaultEdiVolume();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching EDI volume, returning defaults");
            return GetDefaultEdiVolume();
        }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    private static OperationalAlerts GetDefaultAlerts()
    {
        return new OperationalAlerts
        {
            WorkQueueCount = 40,
            PendingRfais = 5,
            AppealsDueThisWeek = 5,
            ApproachingFilingLimit = 3
        };
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    private static EdiVolumeSummary GetDefaultEdiVolume()
    {
        return new EdiVolumeSummary
        {
            Claims837Received = 142,
            Era835Generated = 87,
            Eligibility270271 = 318,
            PriorAuth278 = 24
        };
    }
}

public class AttachmentService : IAttachmentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttachmentService> _logger;
    private readonly ITokenAcquisition _tokenAcquisition;

    public AttachmentService(HttpClient httpClient, IConfiguration configuration, ILogger<AttachmentService> logger, ITokenAcquisition tokenAcquisition)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
    }

    public async Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            var attachments = await _httpClient.GetFromJsonAsync<List<AttachmentInfo>>($"{baseUrl}/attachments/authorization/{authorizationId}");
            return attachments ?? new List<AttachmentInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(streamContent, "file", fileName);
            content.Add(new StringContent(authorizationId), "authorizationId");

            var response = await _httpClient.PostAsync($"{baseUrl}/attachments/upload", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UploadAttachmentResponse>();
            return result?.AttachmentId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            var response = await _httpClient.GetAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task DeleteAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var response = await _httpClient.DeleteAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    private async Task SetBearerTokenAsync()
    {
        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Attachments.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private class UploadAttachmentResponse
    {
        public string AttachmentId { get; set; } = string.Empty;
    }
}

public class SponsorService : ISponsorService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SponsorService> _logger;

    public SponsorService(HttpClient httpClient, IConfiguration configuration, ILogger<SponsorService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<SponsorSummary>> SearchSponsorsAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var sponsors = await _httpClient.GetFromJsonAsync<List<SponsorSummary>>($"{baseUrl}/sponsors?search={searchTerm}");
            return sponsors ?? new List<SponsorSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<SponsorDetails>($"{baseUrl}/sponsors/{sponsorId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task<string> CreateSponsorAsync(CreateSponsorRequest request)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/sponsors", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateSponsorResponse>();
            return result?.SponsorId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task UpdateSponsorAsync(string sponsorId, UpdateSponsorRequest request)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/sponsors/{sponsorId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    private class CreateSponsorResponse
    {
        public string SponsorId { get; set; } = string.Empty;
    }
}

public class ReferenceDataService : IReferenceDataService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReferenceDataService> _logger;

    public ReferenceDataService(HttpClient httpClient, IConfiguration configuration, ILogger<ReferenceDataService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MedicalCode>> SearchCodesAsync(string? codeSystem = null, string? searchTerm = null)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            var query = $"{baseUrl}/codes?";
            if (!string.IsNullOrEmpty(codeSystem))
                query += $"codeSystem={codeSystem}&";
            if (!string.IsNullOrEmpty(searchTerm))
                query += $"search={searchTerm}";

            var codes = await _httpClient.GetFromJsonAsync<List<MedicalCode>>(query);
            return codes ?? new List<MedicalCode>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<MedicalCodeDetails?> GetCodeDetailsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MedicalCodeDetails>($"{baseUrl}/codes/{codeSystem}/{code}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<List<string>> GetCodeSystemsAsync()
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            var systems = await _httpClient.GetFromJsonAsync<List<string>>($"{baseUrl}/code-systems");
            return systems ?? new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<CodeUsageStats> GetCodeUsageStatsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<CodeUsageStats>($"{baseUrl}/codes/{codeSystem}/{code}/usage");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }
}

public class TenantService : ITenantService
{
    private readonly IMongoCollection<TenantSubscription> _tenantsCollection;
    private readonly IMongoCollection<BsonDocument> _membersCollection;
    private readonly IMongoCollection<BsonDocument> _tenantUsersCollection;
    private readonly ILogger<TenantService> _logger;

    public TenantService(IMongoClient mongoClient, IConfiguration configuration, ILogger<TenantService> logger)
    {
        _logger = logger;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _tenantsCollection = db.GetCollection<TenantSubscription>(
            configuration["MongoDB:TenantsCollection"] ?? "Tenants");
        _tenantUsersCollection = db.GetCollection<BsonDocument>("TenantUsers");
        _membersCollection = db.GetCollection<BsonDocument>(
            configuration["MongoDB:MembersCollection"] ?? "Members");
    }

    public async Task<TenantSubscription?> GetSubscriptionByAzureTenantIdAsync(string azureTenantId)
    {
        _logger.LogInformation("Looking up subscription for Azure Tenant ID: {TenantId}", azureTenantId);

        if (string.IsNullOrEmpty(azureTenantId) || azureTenantId == "common")
            return null;

        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var tenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

        if (tenant != null)
            _logger.LogInformation("Found subscription for tenant {TenantId}: {OrgName} ({Status})",
                azureTenantId, tenant.OrganizationName, tenant.SubscriptionStatus);
        else
            _logger.LogInformation("No subscription found for Azure Tenant ID: {TenantId}", azureTenantId);

        return tenant;
    }

    public async Task<TenantSubscription?> GetDemoTenantAsync()
    {
        try
        {
            _logger.LogInformation("Fetching demo tenant");

            var filter = Builders<TenantSubscription>.Filter.Eq(t => t.IsDemo, true);
            var demoTenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

            if (demoTenant != null)
                return demoTenant;

            _logger.LogWarning("No demo tenant found in MongoDB, returning default");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching demo tenant from MongoDB");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
    }

    public async Task<bool> IsMemberOfTenantAsync(string azureTenantId, string userEmail)
    {
        try
        {
            _logger.LogInformation("Checking if {Email} is member of tenant {TenantId}", userEmail, azureTenantId);

            if (string.IsNullOrEmpty(userEmail) || string.IsNullOrEmpty(azureTenantId))
                return false;

            var tenant = await GetSubscriptionByAzureTenantIdAsync(azureTenantId);
            if (tenant == null)
            {
                _logger.LogWarning("Tenant not found for Azure Tenant ID: {TenantId}", azureTenantId);
                return false;
            }

            if (tenant.AdminEmails.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("User {Email} is admin for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check TenantUsers collection (RBAC users managed by tenant-service)
            var tenantUserFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("emailNormalized", userEmail.ToLowerInvariant()),
                Builders<BsonDocument>.Filter.Eq("status", "Active"));
            var tenantUserCount = await _tenantUsersCollection.CountDocumentsAsync(tenantUserFilter);
            if (tenantUserCount > 0)
            {
                _logger.LogInformation("User {Email} found in TenantUsers for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check Members collection (legacy member records)
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("email", userEmail.ToLowerInvariant()));
            var count = await _membersCollection.CountDocumentsAsync(filter);
            var hasMember = count > 0;

            _logger.LogInformation("User {Email} member status for tenant {TenantId}: {IsMember}",
                userEmail, azureTenantId, hasMember);
            return hasMember;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking membership for {Email} in tenant {TenantId}", userEmail, azureTenantId);
            return false;
        }
    }

    public async Task<string> CreateTenantAsync(CreateTenantRequest request)
    {
        try
        {
            var tenantId = $"tenant-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            var tenant = new TenantSubscription
            {
                TenantId = tenantId,
                AzureTenantId = request.AzureTenantId,
                OrganizationName = request.OrganizationName,
                SubscriptionStatus = request.SubscriptionStatus,
                Tier = request.Tier,
                IsDemo = request.IsDemo,
                TrialEndsAt = request.SubscriptionStatus == "Trial" ? now.AddDays(14) : null,
                CreatedAt = now,
                UpdatedAt = now,
                AdminEmails = request.AdminEmails,
                Notes = request.Notes
            };

            _logger.LogInformation("Creating tenant {TenantId} for organization {OrgName} (Azure: {AzureTenantId})",
                tenantId, request.OrganizationName, request.AzureTenantId);

            await _tenantsCollection.InsertOneAsync(tenant);

            _logger.LogInformation("Successfully created tenant {TenantId} in MongoDB", tenantId);
            return tenantId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create tenant for organization {OrgName}", request.OrganizationName);
            throw;
        }
    }

    public async Task UpdateTenantAsync(string azureTenantId, UpdateTenantRequest request)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var updates = new List<UpdateDefinition<TenantSubscription>>
        {
            Builders<TenantSubscription>.Update.Set(t => t.UpdatedAt, DateTime.UtcNow)
        };

        if (request.OrganizationName != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.OrganizationName, request.OrganizationName));
        if (request.Tier != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.Tier, request.Tier));
        if (request.SubscriptionStatus != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.SubscriptionStatus, request.SubscriptionStatus));
        if (request.AdminEmails != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.AdminEmails, request.AdminEmails));
        if (request.IsDemo.HasValue)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.IsDemo, request.IsDemo.Value));
        updates.Add(Builders<TenantSubscription>.Update.Set(t => t.Notes, request.Notes));

        var update = Builders<TenantSubscription>.Update.Combine(updates);
        await _tenantsCollection.UpdateOneAsync(filter, update);
        _logger.LogInformation("Updated tenant {AzureTenantId}: {OrgName}", azureTenantId, request.OrganizationName);
    }

    public async Task DeleteTenantAsync(string azureTenantId)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        await _tenantsCollection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted tenant {AzureTenantId}", azureTenantId);
    }

    public async Task<List<TenantSubscription>> GetAllSubscriptionsAsync()
    {
        var tenants = await _tenantsCollection
            .Find(Builders<TenantSubscription>.Filter.Empty)
            .SortByDescending(t => t.CreatedAt)
            .ToListAsync();
        return tenants;
    }

    public async Task UpdateSubscriptionStatusAsync(string azureTenantId, string status)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var update = Builders<TenantSubscription>.Update
            .Set(t => t.SubscriptionStatus, status)
            .Set(t => t.UpdatedAt, DateTime.UtcNow);
        await _tenantsCollection.UpdateOneAsync(filter, update);
        _logger.LogInformation("Updated subscription status for tenant {TenantId} to {Status}", azureTenantId, status);
    }
}

public class SalesInquiryService : ISalesInquiryService
{
    private readonly IMongoCollection<SalesInquiry> _inquiriesCollection;
    private readonly IEmailNotificationService _emailNotificationService;
    private readonly ILogger<SalesInquiryService> _logger;

    public SalesInquiryService(IMongoClient mongoClient, IConfiguration configuration,
        IEmailNotificationService emailNotificationService, ILogger<SalesInquiryService> logger)
    {
        _logger = logger;
        _emailNotificationService = emailNotificationService;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _inquiriesCollection = db.GetCollection<SalesInquiry>(
            configuration["MongoDB:SalesInquiriesCollection"] ?? "SalesInquiries");
    }

    public async Task<string> CreateInquiryAsync(CreateSalesInquiryRequest request)
    {
        try
        {
            var inquiryId = $"inquiry-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            var inquiry = new SalesInquiry
            {
                Id = inquiryId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = request.Phone,
                CompanyName = request.CompanyName,
                JobTitle = request.JobTitle,
                InquiryType = request.InquiryType,
                Message = request.Message,
                Status = "New",
                Source = request.Source,
                CreatedAt = now,
                ContactedAt = null,
                Notes = null
            };

            _logger.LogInformation("Creating sales inquiry {InquiryId} from {Email} at {Company}",
                inquiryId, request.Email, request.CompanyName);

            await _inquiriesCollection.InsertOneAsync(inquiry);

            _logger.LogInformation("Successfully created sales inquiry {InquiryId}", inquiryId);

            await _emailNotificationService.SendSalesInquiryNotificationAsync(inquiry);

            return inquiryId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sales inquiry from {Email}", request.Email);
            throw;
        }
    }

    public async Task<List<SalesInquiry>> GetInquiriesAsync(string? status = null, int limit = 100)
    {
        try
        {
            FilterDefinition<SalesInquiry> filter = status == null
                ? Builders<SalesInquiry>.Filter.Empty
                : Builders<SalesInquiry>.Filter.Eq(i => i.Status, status);

            var results = await _inquiriesCollection
                .Find(filter)
                .SortByDescending(i => i.CreatedAt)
                .Limit(limit)
                .ToListAsync();

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiries");
            return new List<SalesInquiry>();
        }
    }

    public async Task<SalesInquiry?> GetInquiryByIdAsync(string inquiryId)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            return await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiry {InquiryId}", inquiryId);
            return null;
        }
    }

    public async Task UpdateInquiryStatusAsync(string inquiryId, string status, string? notes = null)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            var inquiry = await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();

            if (inquiry == null)
                throw new InvalidOperationException($"Inquiry {inquiryId} not found");

            var updates = new List<UpdateDefinition<SalesInquiry>>
            {
                Builders<SalesInquiry>.Update.Set(i => i.Status, status)
            };

            if (notes != null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.Notes, notes));

            if (status == "Contacted" && inquiry.ContactedAt == null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.ContactedAt, DateTime.UtcNow));

            var combinedUpdate = Builders<SalesInquiry>.Update.Combine(updates);
            await _inquiriesCollection.UpdateOneAsync(filter, combinedUpdate);

            _logger.LogInformation("Updated sales inquiry {InquiryId} status to {Status}", inquiryId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sales inquiry {InquiryId}", inquiryId);
            throw;
        }
    }
}

public class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationService(IConfiguration configuration, ILogger<SmtpEmailNotificationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendSalesInquiryNotificationAsync(SalesInquiry inquiry)
    {
        var smtpHost = _configuration["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogWarning("SMTP host is not configured. Skipping email notification for inquiry {InquiryId}", inquiry.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(inquiry.Email) || !IsValidEmail(inquiry.Email))
        {
            _logger.LogWarning("Invalid submitter email for inquiry {InquiryId}. Skipping confirmation email.", inquiry.Id);
        }

        var smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out var port) ? port : 587;
        var enableSsl = !string.Equals(_configuration["Email:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);
        var fromAddress = _configuration["Email:FromAddress"] ?? "noreply@cloudhealthoffice.com";
        var salesTeamAddress = _configuration["Email:SalesTeamAddress"] ?? "sales@cloudhealthoffice.com";
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = enableSsl,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : CredentialCache.DefaultNetworkCredentials
        };

        try
        {
            using var salesNotification = BuildSalesTeamEmail(fromAddress, salesTeamAddress, inquiry);
            await client.SendMailAsync(salesNotification);
            _logger.LogInformation("Sales team notification sent for inquiry {InquiryId}", inquiry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send sales team notification for inquiry {InquiryId}", inquiry.Id);
        }

        if (!string.IsNullOrWhiteSpace(inquiry.Email) && IsValidEmail(inquiry.Email))
        {
            try
            {
                using var confirmation = BuildConfirmationEmail(fromAddress, inquiry);
                await client.SendMailAsync(confirmation);
                _logger.LogInformation("Confirmation email sent to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static MailMessage BuildSalesTeamEmail(string from, string to, SalesInquiry inquiry)
    {
        var body =
            $"New Sales Inquiry Received\n\n" +
            $"Inquiry ID:  {inquiry.Id}\n" +
            $"Submitted:   {inquiry.CreatedAt:yyyy-MM-dd HH:mm} UTC\n\n" +
            $"Contact Information\n" +
            $"-------------------\n" +
            $"Name:        {inquiry.FirstName} {inquiry.LastName}\n" +
            $"Email:       {inquiry.Email}\n" +
            $"Phone:       {inquiry.Phone ?? "Not provided"}\n" +
            $"Company:     {inquiry.CompanyName}\n" +
            $"Job Title:   {inquiry.JobTitle ?? "Not provided"}\n\n" +
            $"Inquiry Details\n" +
            $"---------------\n" +
            $"Type:        {inquiry.InquiryType}\n" +
            $"Message:\n{inquiry.Message}\n\n" +
            $"Source: {inquiry.Source}\n\n" +
            $"Reply directly to this email to reach the prospect.";

        var message = new MailMessage(from, to)
        {
            Subject = $"[Cloud Health Office] New Sales Inquiry from {inquiry.CompanyName} – {inquiry.InquiryType}",
            Body = body,
            IsBodyHtml = false
        };
        message.ReplyToList.Add(new MailAddress(inquiry.Email, $"{inquiry.FirstName} {inquiry.LastName}"));
        return message;
    }

    private static MailMessage BuildConfirmationEmail(string from, SalesInquiry inquiry)
    {
        var body =
            $"Hi {inquiry.FirstName},\n\n" +
            $"Thank you for reaching out to Cloud Health Office!\n\n" +
            $"We have received your inquiry and our sales team will be in touch within 1 business day.\n\n" +
            $"Your reference ID is: {inquiry.Id}\n\n" +
            $"Inquiry Summary\n" +
            $"---------------\n" +
            $"Type:    {inquiry.InquiryType}\n" +
            $"Company: {inquiry.CompanyName}\n\n" +
            $"If you have urgent questions in the meantime, please email us at sales@cloudhealthoffice.com.\n\n" +
            $"Best regards,\n" +
            $"The Cloud Health Office Sales Team";

        return new MailMessage(from, inquiry.Email)
        {
            Subject = $"[Cloud Health Office] We received your inquiry – {inquiry.Id}",
            Body = body,
            IsBodyHtml = false
        };
    }
}

// OperatingModeService intentionally returns defaults when tenant-service is unreachable.
// Missing config should default to Replace mode, not error.
public class OperatingModeService : IOperatingModeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatingModeService> _logger;

    public OperatingModeService(HttpClient httpClient, IConfiguration configuration, ILogger<OperatingModeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId)
    {
        var baseUrl = _configuration["Services:TenantService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<OperatingModeConfiguration>(
                $"{baseUrl}/v1/tenants/{tenantId}/operating-mode");
            if (result != null)
                return NormalizeConfiguration(result, tenantId);

            return GetDefaultConfiguration(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch operating mode for tenant {TenantId}, returning defaults", tenantId);
            return GetDefaultConfiguration(tenantId);
        }
    }

    private static OperatingModeConfiguration NormalizeConfiguration(OperatingModeConfiguration config, string tenantId)
    {
        var merged = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in config.Engines)
        {
            merged[kvp.Key] = kvp.Value;
        }

        config.TenantId = string.IsNullOrEmpty(config.TenantId) ? tenantId : config.TenantId;
        config.Engines = merged;
        return config;
    }

    private static OperatingModeConfiguration GetDefaultConfiguration(string tenantId)
    {
        return new OperatingModeConfiguration
        {
            TenantId = tenantId,
            Engines = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase),
            UpdatedAt = null
        };
    }
}

// ── EDI Operations Service ─────────────────────────────────────────────

public class EdiOperationsService : IEdiOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EdiOperationsService> _logger;

    public EdiOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EdiOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Edi834Batch>> Get834BatchesAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/834-batches" +
                (from.HasValue ? $"?from={from:yyyy-MM-dd}" : "") +
                (to.HasValue ? (from.HasValue ? $"&to={to:yyyy-MM-dd}" : $"?to={to:yyyy-MM-dd}") : "");
            var result = await _httpClient.GetFromJsonAsync<List<Edi834Batch>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<Enrollment834Record>> Get834BatchRecordsAsync(string batchId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/edi/834-batches/{batchId}/records");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task Resolve834RecordAsync(Edi834ResolutionRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/edi/834-batches/{request.BatchId}/resolve", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<ClaimAcknowledgmentSummary>> Get277CaAcknowledgmentsAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ClaimAcknowledgmentSummary>>($"{baseUrl}/edi/277ca");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<Stream> Download277CaAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/claims/{claimId}/277ca");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<EraSummary>> GetErasAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<EraSummary>>($"{baseUrl}/payments/eras");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraAsync(string paymentId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payments/{paymentId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<List<EdiTransactionHistoryItem>> GetTransactionHistoryAsync(DateTime? from, DateTime? to, string? transactionType, string? partnerId, string? status, int pageNumber, int pageSize)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/history?page={pageNumber}&pageSize={pageSize}" +
                (transactionType != null ? $"&type={transactionType}" : "") +
                (status != null ? $"&status={status}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<EdiTransactionHistoryItem>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }
}

// ── Payment Run Service ─────────────────────────────────────────────────

public class PaymentRunService : IPaymentRunService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentRunService> _logger;

    public PaymentRunService(HttpClient httpClient, IConfiguration configuration, ILogger<PaymentRunService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PaymentRunSummary>>($"{baseUrl}/payment-runs?limit={limit}");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<PaymentRunDetails>($"{baseUrl}/payment-runs/{runId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/payment-runs", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateRunResponse>();
            return result?.RunId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task CancelPaymentRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/payment-runs/{runId}/cancel", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraForRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payment-runs/{runId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    private class CreateRunResponse { public string RunId { get; set; } = string.Empty; }
}

// ── Premium Billing Service ────────────────────────────────────────────

public class PremiumBillingService : IPremiumBillingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PremiumBillingService> _logger;

    public PremiumBillingService(HttpClient httpClient, IConfiguration configuration, ILogger<PremiumBillingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<BillingCycle>> GetBillingCyclesAsync(string? sponsorId = null, string? status = null)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var url = $"{baseUrl}/billing-cycles" + (sponsorId != null ? $"?sponsorId={sponsorId}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<BillingCycle>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<BillingCycleDetails?> GetBillingCycleByIdAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<BillingCycleDetails>($"{baseUrl}/billing-cycles/{cycleId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<string> GenerateInvoiceAsync(CreateInvoiceRequest request)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/billing-cycles", request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreateCycleResponse>())?.CycleId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<List<PremiumRate>> GetPremiumRatesAsync(string? planId = null)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var url = $"{baseUrl}/premium-rates" + (planId != null ? $"?planId={planId}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<PremiumRate>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task UpdatePremiumRateAsync(string rateId, decimal newRate, DateTime effectiveDate)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/premium-rates/{rateId}", new { Rate = newRate, EffectiveDate = effectiveDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task MarkCycleAsPaidAsync(string cycleId, DateTime paidDate)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/billing-cycles/{cycleId}/mark-paid", new { PaidDate = paidDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<Stream> DownloadInvoiceAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/billing-cycles/{cycleId}/invoice");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    private class CreateCycleResponse { public string CycleId { get; set; } = string.Empty; }
}

// ── Reporting Service ───────────────────────────────────────────────────

public class ReportingService : IReportingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportingService> _logger;

    public ReportingService(HttpClient httpClient, IConfiguration configuration, ILogger<ReportingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClaimsSummaryReport> GetClaimsSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/claims-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<ClaimsSummaryReport>()
                ?? throw new Exception("Empty response from claims summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/payment-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<PaymentSummaryReport>()
                ?? throw new Exception("Empty response from payment summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/eligibility-stats", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<EligibilityStatsReport>()
                ?? throw new Exception("Empty response from eligibility stats report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Eligibility Service");
            throw new ServiceUnavailableException("Eligibility Service", ex);
        }
    }

    public async Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/auth-approval", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<AuthApprovalReport>()
                ?? throw new Exception("Empty response from auth approval report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/provider-performance", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<List<ClaimsByProvider>>() ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Work Queue Service
// ---------------------------------------------------------------------------

public class WorkQueueService : IWorkQueueService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkQueueService> _logger;

    public WorkQueueService(HttpClient httpClient, IConfiguration configuration, ILogger<WorkQueueService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WorkQueueSummary> GetQueueSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<WorkQueueSummary>($"{baseUrl}/work-queue/summary");
            return summary ?? new WorkQueueSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<WorkQueueItem>> GetQueueItemsAsync(string? queueType = null,
        string? assignedTo = null, int limit = 100)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/work-queue/items?limit={limit}";
            if (!string.IsNullOrEmpty(queueType)) url += $"&queueType={Uri.EscapeDataString(queueType)}";
            if (!string.IsNullOrEmpty(assignedTo)) url += $"&assignedTo={Uri.EscapeDataString(assignedTo)}";
            var items = await _httpClient.GetFromJsonAsync<List<WorkQueueItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task AssignClaimAsync(string claimId, string assignTo)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/work-queue/{Uri.EscapeDataString(claimId)}/assign",
                new { AssignTo = assignTo });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task OverrideAsync(string claimId, string overrideReason)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/work-queue/{Uri.EscapeDataString(claimId)}/override",
                new { Reason = overrideReason });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Enrollment Operations Service
// ---------------------------------------------------------------------------

public class EnrollmentOperationsService : IEnrollmentOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EnrollmentOperationsService> _logger;

    public EnrollmentOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EnrollmentOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EnrollmentDailySummary> GetTodaySummaryAsync()
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<EnrollmentDailySummary>($"{baseUrl}/enrollment-ops/summary/today");
            return summary ?? new EnrollmentDailySummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<EnrollmentFile>>($"{baseUrl}/enrollment-ops/files?days={days}");
            return files ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var detail = await _httpClient.GetFromJsonAsync<EnrollmentFileDetail>($"{baseUrl}/enrollment-ops/files/{Uri.EscapeDataString(fileId)}");
            return detail ?? throw new Exception($"File {fileId} not found");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Appeals Service
// ---------------------------------------------------------------------------

public class AppealsService : IAppealsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppealsService> _logger;

    public AppealsService(HttpClient httpClient, IConfiguration configuration, ILogger<AppealsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AppealsSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<AppealsSummary>($"{baseUrl}/appeals/summary");
            return summary ?? new AppealsSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrEmpty(appealId)) queryParts.Add($"appealId={Uri.EscapeDataString(appealId)}");
            if (!string.IsNullOrEmpty(memberId)) queryParts.Add($"memberId={Uri.EscapeDataString(memberId)}");
            if (!string.IsNullOrEmpty(originalClaimId)) queryParts.Add($"originalClaimId={Uri.EscapeDataString(originalClaimId)}");
            var query = string.Join("&", queryParts);
            var results = await _httpClient.GetFromJsonAsync<List<AppealSummary>>($"{baseUrl}/appeals/search?{query}");
            return results ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<AppealDetails?> GetAppealByIdAsync(string appealId)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AppealDetails>($"{baseUrl}/appeals/{Uri.EscapeDataString(appealId)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Correspondence Service
// ---------------------------------------------------------------------------

public class CorrespondenceService : ICorrespondenceService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CorrespondenceService> _logger;

    public CorrespondenceService(HttpClient httpClient, IConfiguration configuration, ILogger<CorrespondenceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CorrespondenceSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<CorrespondenceSummary>($"{baseUrl}/correspondence/summary");
            return summary ?? new CorrespondenceSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<CorrespondenceItem>> GetQueueAsync(string? type = null,
        string? status = null, int limit = 50)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/correspondence/queue?limit={limit}";
            if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";
            if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";
            var items = await _httpClient.GetFromJsonAsync<List<CorrespondenceItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var items = await _httpClient.GetFromJsonAsync<List<RfaiTrackingItem>>($"{baseUrl}/correspondence/rfais/outstanding");
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

public class PricingApiService : IPricingApiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PricingApiService> _logger;

    public PricingApiService(HttpClient httpClient, IConfiguration configuration, ILogger<PricingApiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:PricingApi"] ?? "http://pricing-api.cloudhealthoffice";
    private string AdminSecret => _configuration["PricingApi:AdminSecret"] ?? "";

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Admin-Secret", AdminSecret);
        return request;
    }

    public async Task<List<PricingApiKey>> GetApiKeysAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Get, $"{BaseUrl}/api/v1/admin/api-keys");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var keys = await response.Content.ReadFromJsonAsync<List<PricingApiKey>>();
            return keys ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<PricingApiKey> CreateApiKeyAsync(string tenantName, string contactEmail, string tier)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys");
            request.Content = JsonContent.Create(new { tenantName, contactEmail, tier });
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var key = await response.Content.ReadFromJsonAsync<PricingApiKey>();
            return key ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task DeactivateApiKeyAsync(string apiKey)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Delete, $"{BaseUrl}/api/v1/admin/api-keys/{Uri.EscapeDataString(apiKey)}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task ResetUsageAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys/reset-usage");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<List<PricingFeeScheduleInfo>> GetFeeSchedulesAsync()
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PricingFeeScheduleInfo>>($"{BaseUrl}/api/v1/fee-schedules");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<FeeScheduleUploadResult> UploadFeeScheduleAsync(string type, int year, Stream csvStream, string fileName, decimal? baseRate = null)
    {
        try
        {
            var url = $"{BaseUrl}/api/v1/admin/fee-schedules/upload/{type.ToLowerInvariant()}?year={year}";
            if (baseRate.HasValue)
                url += $"&baseRate={baseRate.Value}";

            var request = CreateAdminRequest(HttpMethod.Post, url);
            var content = new MultipartFormDataContent();
            content.Add(new StreamContent(csvStream), "file", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<FeeScheduleUploadResult>();
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task SeedDemoDataAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/fee-schedules/seed-demo");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }
}

// ── Capitation Service ────────────────────────────────────────────────────

public class CapitationService : ICapitationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CapitationService> _logger;

    public CapitationService(HttpClient httpClient, IConfiguration configuration, ILogger<CapitationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:CapitationService"] ?? "http://capitation-service.cloudhealthoffice/api";

    // Contracts
    public async Task<List<CapitationContractSummary>> GetContractsAsync(string? npi = null, string? status = null, string? lob = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapitationContractSummary>>($"{BaseUrl}/v1/capitation/contracts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationContractSummary?> GetContractByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationContractSummary>($"{BaseUrl}/v1/capitation/contracts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateContractAsync(CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/contracts", contract);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapitationContractSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task UpdateContractAsync(string id, CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}", contract);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ActivateContractAsync(string id)
    {
        try
        {
            var response = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/contracts/{id}/activate", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}/terminate",
                new { Reason = reason, TerminationDate = terminationDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Runs
    public async Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null)
    {
        try
        {
            var qs = new List<string>();
            if (from.HasValue) qs.Add($"from={from.Value:O}");
            if (to.HasValue) qs.Add($"to={to.Value:O}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapRunSummary>>($"{BaseUrl}/v1/capitation/runs{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary?> GetRunByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapRunSummary>($"{BaseUrl}/v1/capitation/runs/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateRunAsync(CreateCapRunRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/runs", request);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapRunSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary> ExecuteRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/capitation/runs/{id}/execute", null);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapRunSummary>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task CancelRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"{BaseUrl}/v1/capitation/runs/{id}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Statements
    public async Task<List<CapStatementSummary>> GetStatementsAsync(string? npi = null, DateTime? periodFrom = null, DateTime? periodTo = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (periodFrom.HasValue) qs.Add($"periodFrom={periodFrom.Value:O}");
            if (periodTo.HasValue) qs.Add($"periodTo={periodTo.Value:O}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapStatementSummary?> GetStatementByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapStatementSummary>($"{BaseUrl}/v1/capitation/statements/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetStatementsByRunAsync(string runId)
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/runs/{runId}/statements") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetUnpaidStatementsAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements/unpaid") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ApproveStatementAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/statements/{id}/approve", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task VoidStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/void", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task HoldStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/hold", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationPeriodSummaryDto> GetPeriodSummaryAsync(DateTime period)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationPeriodSummaryDto>($"{BaseUrl}/v1/capitation/statements/summary?period={period:O}") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Disbursements
    public async Task<string> InitiateDisbursementAsync(string statementId, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements",
                new { StatementId = statementId, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return "ok";
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapDisbursementBatchResult> InitiateBatchDisbursementAsync(List<string> statementIds, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements/batch",
                new { StatementIds = statementIds, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapDisbursementBatchResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }
}
