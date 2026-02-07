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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching members, returning mock data");
            return GetMockMembers(searchTerm);
        }
    }

    public async Task<MemberDetails?> GetMemberByIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberDetails>($"{baseUrl}/members/{memberId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching member {MemberId}, returning mock data", memberId);
            return GetMockMemberDetails(memberId);
        }
    }

    private List<MemberSummary> GetMockMembers(string searchTerm)
    {
        var allMembers = new List<MemberSummary>
        {
            new() { MemberId = "MBR-2024-001", FirstName = "Sarah", LastName = "Johnson", DateOfBirth = new DateTime(1985, 3, 15), CoverageStatus = "Active" },
            new() { MemberId = "MBR-2024-002", FirstName = "Michael", LastName = "Chen", DateOfBirth = new DateTime(1992, 7, 22), CoverageStatus = "Active" },
            new() { MemberId = "MBR-2024-003", FirstName = "Emily", LastName = "Rodriguez", DateOfBirth = new DateTime(1978, 11, 8), CoverageStatus = "Active" },
            new() { MemberId = "MBR-2024-004", FirstName = "David", LastName = "Thompson", DateOfBirth = new DateTime(1990, 5, 30), CoverageStatus = "Pending" },
            new() { MemberId = "MBR-2024-005", FirstName = "Jennifer", LastName = "Williams", DateOfBirth = new DateTime(1982, 9, 14), CoverageStatus = "Active" },
            new() { MemberId = "MBR-2024-006", FirstName = "Robert", LastName = "Garcia", DateOfBirth = new DateTime(1975, 2, 28), CoverageStatus = "Inactive" },
            new() { MemberId = "MBR-2024-007", FirstName = "Lisa", LastName = "Martinez", DateOfBirth = new DateTime(1988, 12, 3), CoverageStatus = "Active" },
            new() { MemberId = "MBR-2024-008", FirstName = "James", LastName = "Anderson", DateOfBirth = new DateTime(1995, 6, 17), CoverageStatus = "Active" },
        };

        var search = searchTerm.ToLowerInvariant();
        return allMembers.Where(m => 
            m.MemberId.ToLowerInvariant().Contains(search) ||
            m.FirstName.ToLowerInvariant().Contains(search) ||
            m.LastName.ToLowerInvariant().Contains(search) ||
            m.DateOfBirth.ToString("MM/dd/yyyy").Contains(search)
        ).ToList();
    }

    private MemberDetails GetMockMemberDetails(string memberId)
    {
        var mockMembers = new Dictionary<string, MemberDetails>
        {
            ["MBR-2024-001"] = new()
            {
                MemberId = "MBR-2024-001",
                FirstName = "Sarah",
                LastName = "Johnson",
                DateOfBirth = new DateTime(1985, 3, 15),
                Gender = "Female",
                CoverageStatus = "Active",
                Email = "sarah.johnson@email.com",
                Phone = "(555) 123-4567",
                Address = new Address
                {
                    Street = "123 Main Street",
                    City = "Seattle",
                    State = "WA",
                    ZipCode = "98101"
                }
            },
            ["MBR-2024-002"] = new()
            {
                MemberId = "MBR-2024-002",
                FirstName = "Michael",
                LastName = "Chen",
                DateOfBirth = new DateTime(1992, 7, 22),
                Gender = "Male",
                CoverageStatus = "Active",
                Email = "michael.chen@email.com",
                Phone = "(555) 234-5678",
                Address = new Address
                {
                    Street = "456 Oak Avenue",
                    City = "Portland",
                    State = "OR",
                    ZipCode = "97201"
                }
            },
        };

        return mockMembers.TryGetValue(memberId, out var member) 
            ? member 
            : new MemberDetails
            {
                MemberId = memberId,
                FirstName = "John",
                LastName = "Doe",
                DateOfBirth = new DateTime(1990, 1, 1),
                Gender = "Unknown",
                CoverageStatus = "Active",
                Email = "john.doe@email.com",
                Phone = "(555) 000-0000",
                Address = new Address
                {
                    Street = "123 Unknown St",
                    City = "Unknown",
                    State = "WA",
                    ZipCode = "00000"
                }
            };
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching coverage for member {MemberId}, returning mock data", memberId);
            return GetMockCoverage(memberId);
        }
    }

    private List<Coverage> GetMockCoverage(string memberId)
    {
        return new List<Coverage>
        {
            new()
            {
                CoverageId = $"COV-{memberId}-001",
                PlanName = "Premium Health Plan",
                GroupNumber = "GRP-12345",
                EffectiveDate = DateTime.Now.AddYears(-2),
                TerminationDate = null,
                Status = "Active"
            },
            new()
            {
                CoverageId = $"COV-{memberId}-002",
                PlanName = "Dental Plus",
                GroupNumber = "GRP-12345-D",
                EffectiveDate = DateTime.Now.AddYears(-1),
                TerminationDate = null,
                Status = "Active"
            }
        };
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
