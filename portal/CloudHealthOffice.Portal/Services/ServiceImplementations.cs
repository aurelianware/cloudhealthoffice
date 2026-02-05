using System.Net.Http.Json;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching recent claims");
            return new List<ClaimSummary>();
        }
    }

    public async Task<ClaimDetails?> GetClaimByIdAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ClaimDetails>($"{baseUrl}/claims/{claimId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching claim {ClaimId}", claimId);
            return null;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting claim");
            throw;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking eligibility");
            throw;
        }
    }
}

public class MemberService : IMemberService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MemberService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<MemberSummary>> SearchMembersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var members = await _httpClient.GetFromJsonAsync<List<MemberSummary>>($"{baseUrl}/members/search?q={searchTerm}");
        return members ?? new List<MemberSummary>();
    }

    public async Task<MemberDetails?> GetMemberByIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        return await _httpClient.GetFromJsonAsync<MemberDetails>($"{baseUrl}/members/{memberId}");
    }
}

public class CoverageService : ICoverageService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public CoverageService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:CoverageService"];
        var coverage = await _httpClient.GetFromJsonAsync<List<Coverage>>($"{baseUrl}/coverage/member/{memberId}");
        return coverage ?? new List<Coverage>();
    }
}

public class AuthorizationService : IAuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public AuthorizationService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        var url = string.IsNullOrEmpty(memberId) 
            ? $"{baseUrl}/authorizations" 
            : $"{baseUrl}/authorizations?memberId={memberId}";
        var auths = await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>(url);
        return auths ?? new List<AuthorizationSummary>();
    }
}

public class ProviderService : IProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public ProviderService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        var providers = await _httpClient.GetFromJsonAsync<List<ProviderSummary>>($"{baseUrl}/providers/search?q={searchTerm}");
        return providers ?? new List<ProviderSummary>();
    }
}

public class BenefitPlanService : IBenefitPlanService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public BenefitPlanService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<BenefitPlan>> GetBenefitPlansAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlan>>($"{baseUrl}/benefit-plans");
        return plans ?? new List<BenefitPlan>();
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
        var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?limit={limit}");
        return workflows ?? new List<WorkflowRun>();
    }

    public async Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        return await _httpClient.GetFromJsonAsync<WorkflowDetails>($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}");
    }
}

public class MetricsService : IMetricsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MetricsService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        // TODO: Query Prometheus for real metrics
        // For now, return sample data
        return new DashboardMetrics
        {
            TotalClaims = 1247,
            ClaimsTrend = 0.085,
            ApprovalRate = 0.923,
            AvgProcessingTimeMs = 387,
            TotalPayerAmount = 2_847_392.50m,
            ApprovedClaims = 1151,
            DeniedClaims = 96,
            PendingClaims = 23
        };
    }
}
