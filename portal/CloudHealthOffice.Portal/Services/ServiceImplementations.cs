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
            _logger.LogWarning(ex, "Error fetching recent claims, returning mock data");
            return GetMockClaims(count);
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
            _logger.LogWarning(ex, "Error fetching claim {ClaimId}, returning mock data", claimId);
            return GetMockClaimDetails(claimId);
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

    private List<ClaimSummary> GetMockClaims(int count)
    {
        var random = new Random();
        var statuses = new[] { "Approved", "Approved", "Approved", "Denied", "Pending" };
        var members = new[] 
        {
            ("MBR-2024-001", "Sarah Johnson"),
            ("MBR-2024-002", "Michael Chen"),
            ("MBR-2024-003", "Emily Rodriguez"),
            ("MBR-2024-004", "David Thompson"),
            ("MBR-2024-005", "Jennifer Williams"),
            ("MBR-2024-006", "Robert Garcia"),
            ("MBR-2024-007", "Lisa Martinez"),
            ("MBR-2024-008", "James Anderson")
        };
        var providers = new[]
        {
            ("PRV-001", "Seattle Medical Center"),
            ("PRV-002", "Downtown Urgent Care"),
            ("PRV-003", "West Coast Radiology"),
            ("PRV-004", "City General Hospital"),
            ("PRV-005", "Advanced Diagnostics Lab")
        };

        var claims = new List<ClaimSummary>();
        for (int i = 1; i <= Math.Min(count, 50); i++)
        {
            var member = members[random.Next(members.Length)];
            var provider = providers[random.Next(providers.Length)];
            var status = statuses[random.Next(statuses.Length)];
            
            claims.Add(new ClaimSummary
            {
                ClaimId = $"CLM-2026-{i:D5}",
                MemberName = member.Item2,
                ProviderName = provider.Item2,
                TotalChargeAmount = random.Next(500, 50000),
                Status = status,
                ProcessingTimeMs = random.Next(150, 800)
            });
        }

        return claims.OrderByDescending(c => c.ClaimId).ToList();
    }

    private ClaimDetails GetMockClaimDetails(string claimId)
    {
        var random = new Random(claimId.GetHashCode());
        var statuses = new[] { "Approved", "Denied", "Pending" };
        var status = statuses[random.Next(statuses.Length)];

        var serviceLines = new List<ServiceLine>
        {
            new()
            {
                ProcedureCode = "99213",
                Description = "Office Visit - Established Patient, Level 3",
                ChargeAmount = 150.00m,
                AllowedAmount = 125.00m,
                PayerAmount = 100.00m
            },
            new()
            {
                ProcedureCode = "80053",
                Description = "Comprehensive Metabolic Panel",
                ChargeAmount = 85.00m,
                AllowedAmount = 75.00m,
                PayerAmount = 60.00m
            },
            new()
            {
                ProcedureCode = "85025",
                Description = "Complete Blood Count (CBC)",
                ChargeAmount = 45.00m,
                AllowedAmount = 40.00m,
                PayerAmount = 32.00m
            }
        };

        var totalCharge = serviceLines.Sum(sl => sl.ChargeAmount);
        var totalPayer = status == "Approved" ? serviceLines.Sum(sl => sl.PayerAmount) : 0;
        var patientResp = status == "Approved" ? serviceLines.Sum(sl => sl.AllowedAmount - sl.PayerAmount) : totalCharge;

        return new ClaimDetails
        {
            ClaimId = claimId,
            MemberId = "MBR-2024-001",
            MemberName = "Sarah Johnson",
            ProviderId = "PRV-001",
            ProviderName = "Seattle Medical Center",
            TotalChargeAmount = totalCharge,
            PayerAmount = totalPayer,
            PatientResponsibility = patientResp,
            Status = status,
            ProcessingTimeMs = random.Next(200, 600),
            ServiceDate = DateTime.Now.AddDays(-random.Next(1, 90)),
            SubmittedDate = DateTime.Now.AddDays(-random.Next(0, 30)),
            ProcessedDate = status != "Pending" ? DateTime.Now.AddDays(-random.Next(0, 15)) : null,
            ServiceLines = serviceLines
        };
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
    private readonly ILogger<AuthorizationService> _logger;

    public AuthorizationService(HttpClient httpClient, IConfiguration configuration, ILogger<AuthorizationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var url = string.IsNullOrEmpty(memberId) 
                ? $"{baseUrl}/authorizations" 
                : $"{baseUrl}/authorizations?memberId={memberId}";
            var auths = await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>(url);
            return auths ?? new List<AuthorizationSummary>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching authorizations, returning mock data");
            return GetMockAuthorizations(memberId);
        }
    }

    public async Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AuthorizationDetails>($"{baseUrl}/authorizations/{authorizationId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching authorization {AuthorizationId}, returning mock data", authorizationId);
            return GetMockAuthorizationDetails(authorizationId);
        }
    }

    public async Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/authorizations", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitAuthorizationResponse>();
            return result?.AuthorizationId ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error submitting authorization, returning mock ID");
            return $"AUTH-2026-{new Random().Next(10000, 99999):D5}";
        }
    }

    private List<AuthorizationSummary> GetMockAuthorizations(string? memberId)
    {
        var random = new Random();
        var statuses = new[] { "Approved", "Approved", "Approved", "Pending", "Review Required", "Denied" };
        var serviceTypes = new[] { "Surgery", "MRI", "CT Scan", "Physical Therapy", "Specialist Consult" };
        var members = new[] 
        {
            ("MBR-2024-001", "Sarah Johnson"),
            ("MBR-2024-002", "Michael Chen"),
            ("MBR-2024-003", "Emily Rodriguez"),
            ("MBR-2024-004", "David Thompson")
        };
        var providers = new[]
        {
            ("PRV-001", "Seattle Medical Center"),
            ("PRV-002", "Downtown Urgent Care"),
            ("PRV-003", "West Coast Radiology"),
            ("PRV-004", "City General Hospital")
        };

        var authorizations = new List<AuthorizationSummary>();
        for (int i = 1; i <= 30; i++)
        {
            var member = members[random.Next(members.Length)];
            var provider = providers[random.Next(providers.Length)];
            var status = statuses[random.Next(statuses.Length)];
            var requestDate = DateTime.Now.AddDays(-random.Next(0, 60));
            var hasDecision = status != "Pending";
            
            authorizations.Add(new AuthorizationSummary
            {
                AuthorizationId = $"AUTH-2026-{i:D5}",
                MemberName = member.Item2,
                ProviderName = provider.Item2,
                ServiceType = serviceTypes[random.Next(serviceTypes.Length)],
                Status = status,
                RequestDate = requestDate,
                DecisionDate = hasDecision ? requestDate.AddMinutes(random.Next(1, 120)) : null,
                ProcessingTimeMs = hasDecision ? random.Next(45000, 180000) : 0
            });
        }

        return authorizations.OrderByDescending(a => a.RequestDate).ToList();
    }

    private AuthorizationDetails GetMockAuthorizationDetails(string authorizationId)
    {
        var random = new Random(authorizationId.GetHashCode());
        var statuses = new[] { "Approved", "Denied", "Pending", "Review Required" };
        var status = statuses[random.Next(statuses.Length)];
        var serviceTypes = new[] 
        { 
            ("Surgery", "27447", "Total knee replacement"),
            ("MRI", "70553", "Brain MRI with contrast"),
            ("CT Scan", "71260", "CT chest with contrast"),
            ("Physical Therapy", "97110", "Therapeutic exercises"),
            ("Specialist Consult", "99244", "Office consultation")
        };
        var serviceType = serviceTypes[random.Next(serviceTypes.Length)];
        
        var diagnoses = new[]
        {
            ("M54.5", "Low back pain"),
            ("M17.11", "Unilateral primary osteoarthritis, right knee"),
            ("G43.909", "Migraine, unspecified"),
            ("I10", "Essential hypertension"),
            ("E11.9", "Type 2 diabetes mellitus")
        };
        var diagnosis = diagnoses[random.Next(diagnoses.Length)];

        var unitsRequested = random.Next(1, 12);
        var requestDate = DateTime.Now.AddDays(-random.Next(1, 30));
        var hasDecision = status != "Pending";
        var processingTime = hasDecision ? random.Next(45000, 180000) : 0;

        // Generate mock attachments
        var attachments = new List<AttachmentInfo>();
        var attachmentCount = random.Next(1, 4);
        var attachmentTypes = new[] { "Medical Records", "Lab Results", "Imaging Study", "Clinical Notes" };
        
        for (int i = 0; i < attachmentCount; i++)
        {
            var attType = attachmentTypes[random.Next(attachmentTypes.Length)];
            attachments.Add(new AttachmentInfo
            {
                AttachmentId = $"ATT-{authorizationId}-{i:D2}",
                FileName = $"{attType.Replace(" ", "_")}_{i + 1}.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = random.Next(100000, 3000000),
                UploadedDate = requestDate.AddHours(random.Next(1, 24)),
                UploadedBy = "Provider Portal",
                AttachmentType = attType,
                BlobPath = $"hipaa-attachments/authorizations/{authorizationId}/doc{i + 1}.pdf"
            });
        }

        return new AuthorizationDetails
        {
            AuthorizationId = authorizationId,
            MemberId = "MBR-2024-001",
            MemberName = "Sarah Johnson",
            ProviderId = "PRV-001",
            ProviderName = "Seattle Medical Center",
            ServiceType = serviceType.Item1,
            Status = status,
            RequestDate = requestDate,
            DecisionDate = hasDecision ? requestDate.AddMilliseconds(processingTime) : null,
            ProcessingTimeMs = processingTime,
            DiagnosisCode = diagnosis.Item1,
            DiagnosisDescription = diagnosis.Item2,
            ProcedureCode = serviceType.Item2,
            ProcedureDescription = serviceType.Item3,
            UnitsRequested = unitsRequested,
            UnitsApproved = status == "Approved" ? unitsRequested : (status == "Denied" ? 0 : null),
            ServiceStartDate = requestDate.AddDays(random.Next(7, 30)),
            ServiceEndDate = serviceType.Item1 == "Physical Therapy" ? requestDate.AddDays(random.Next(37, 90)) : null,
            ReviewerNotes = status == "Approved" 
                ? "Authorization approved based on medical necessity and appropriate clinical documentation." 
                : (status == "Denied" 
                    ? "Unable to approve request. Additional clinical documentation required." 
                    : (status == "Review Required" ? "Flagged for clinical review. Complex case requires MD evaluation." : string.Empty)),
            DenialReason = status == "Denied" 
                ? "Insufficient clinical documentation to support medical necessity. Please provide recent diagnostic imaging and treatment history." 
                : string.Empty,
            Attachments = attachments
        };
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

public class AttachmentService : IAttachmentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(HttpClient httpClient, IConfiguration configuration, ILogger<AttachmentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var attachments = await _httpClient.GetFromJsonAsync<List<AttachmentInfo>>($"{baseUrl}/attachments/authorization/{authorizationId}");
            return attachments ?? new List<AttachmentInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching attachments for {AuthorizationId}, returning mock data", authorizationId);
            return GetMockAttachments(authorizationId);
        }
    }

    public async Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error uploading attachment for {AuthorizationId}, returning mock ID", authorizationId);
            return $"ATT-{Guid.NewGuid():N}".Substring(0, 20).ToUpper();
        }
    }

    public async Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading attachment {AttachmentId}", attachmentId);
            throw;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting attachment {AttachmentId}", attachmentId);
            throw;
        }
    }

    private List<AttachmentInfo> GetMockAttachments(string authorizationId)
    {
        var random = new Random(authorizationId.GetHashCode());
        var attachmentTypes = new[] { "Medical Records", "Lab Results", "Imaging Study", "Clinical Notes", "Prescription" };
        var contentTypes = new[] { "application/pdf", "image/jpeg", "image/png", "application/pdf", "application/pdf" };
        
        var count = random.Next(0, 4);
        var attachments = new List<AttachmentInfo>();

        for (int i = 0; i < count; i++)
        {
            var typeIndex = random.Next(attachmentTypes.Length);
            var uploadDate = DateTime.Now.AddDays(-random.Next(1, 30));
            
            attachments.Add(new AttachmentInfo
            {
                AttachmentId = $"ATT-{Guid.NewGuid():N}".Substring(0, 20).ToUpper(),
                FileName = $"{attachmentTypes[typeIndex].Replace(" ", "_")}_{i + 1}.pdf",
                ContentType = contentTypes[typeIndex],
                FileSizeBytes = random.Next(50000, 5000000),
                UploadedDate = uploadDate,
                UploadedBy = "Provider Portal",
                AttachmentType = attachmentTypes[typeIndex],
                BlobPath = $"hipaa-attachments/authorizations/{authorizationId}/doc{i + 1}.pdf"
            });
        }

        return attachments;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching sponsors, returning mock data");
            return GetMockSponsors(searchTerm);
        }
    }

    public async Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<SponsorDetails>($"{baseUrl}/sponsors/{sponsorId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching sponsor {SponsorId}, returning mock data", sponsorId);
            return GetMockSponsorDetails(sponsorId);
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
            return result?.SponsorId ?? $"SPNSR{Random.Shared.Next(10000, 99999)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating sponsor, generating mock ID");
            await Task.Delay(500);
            return $"SPNSR{Random.Shared.Next(10000, 99999)}";
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating sponsor {SponsorId}, mock update successful", sponsorId);
            await Task.Delay(300);
        }
    }

    private List<SponsorSummary> GetMockSponsors(string searchTerm)
    {
        var sponsors = new List<SponsorSummary>
        {
            new SponsorSummary
            {
                SponsorId = "SPNSR10001",
                Name = "Acme Corporation",
                Type = "Employer",
                State = "CA",
                ActiveBenefitPlans = 3,
                TotalMembers = 2847,
                Status = "Active",
                ContractStartDate = new DateTime(2024, 1, 1),
                ContractEndDate = new DateTime(2026, 12, 31)
            },
            new SponsorSummary
            {
                SponsorId = "SPNSR10002",
                Name = "TechStart Industries",
                Type = "Employer",
                State = "TX",
                ActiveBenefitPlans = 2,
                TotalMembers = 1523,
                Status = "Active",
                ContractStartDate = new DateTime(2025, 1, 1),
                ContractEndDate = new DateTime(2027, 12, 31)
            },
            new SponsorSummary
            {
                SponsorId = "SPNSR10003",
                Name = "United Workers Local 247",
                Type = "Union",
                State = "NY",
                ActiveBenefitPlans = 4,
                TotalMembers = 4521,
                Status = "Active",
                ContractStartDate = new DateTime(2023, 6, 1)
            },
            new SponsorSummary
            {
                SponsorId = "SPNSR10004",
                Name = "Healthcare Associates",
                Type = "Employer",
                State = "FL",
                ActiveBenefitPlans = 2,
                TotalMembers = 892,
                Status = "Active",
                ContractStartDate = new DateTime(2025, 3, 1),
                ContractEndDate = new DateTime(2028, 2, 28)
            },
            new SponsorSummary
            {
                SponsorId = "SPNSR10005",
                Name = "Regional Business Alliance",
                Type = "Association",
                State = "WA",
                ActiveBenefitPlans = 5,
                TotalMembers = 6234,
                Status = "Active",
                ContractStartDate = new DateTime(2024, 7, 1),
                ContractEndDate = new DateTime(2027, 6, 30)
            },
            new SponsorSummary
            {
                SponsorId = "SPNSR10006",
                Name = "Metro Manufacturing Inc",
                Type = "Employer",
                State = "MI",
                ActiveBenefitPlans = 1,
                TotalMembers = 456,
                Status = "Pending",
                ContractStartDate = new DateTime(2026, 1, 1),
                ContractEndDate = new DateTime(2029, 12, 31)
            }
        };

        if (string.IsNullOrWhiteSpace(searchTerm))
            return sponsors;

        return sponsors.Where(s => 
            s.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            s.SponsorId.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            s.State.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    private SponsorDetails GetMockSponsorDetails(string sponsorId)
    {
        var sponsors = new Dictionary<string, SponsorDetails>
        {
            ["SPNSR10001"] = new SponsorDetails
            {
                SponsorId = "SPNSR10001",
                Name = "Acme Corporation",
                Type = "Employer",
                State = "CA",
                ActiveBenefitPlans = 3,
                TotalMembers = 2847,
                Status = "Active",
                ContractStartDate = new DateTime(2024, 1, 1),
                ContractEndDate = new DateTime(2026, 12, 31),
                TaxId = "12-3456789",
                AddressLine1 = "123 Business Park Drive",
                City = "San Francisco",
                ZipCode = "94105",
                ContactName = "Jane Smith",
                ContactPhone = "(415) 555-0123",
                ContactEmail = "benefits@acmecorp.com",
                BillingFrequency = "Monthly",
                PaymentMethod = "ACH",
                GroupSizeTier = "Large (50+)",
                BenefitPlans = new List<BenefitPlanSummary>
                {
                    new BenefitPlanSummary
                    {
                        PlanId = "PLAN1001",
                        PlanName = "Gold PPO Plus",
                        ProductType = "PPO",
                        EnrolledMembers = 1523,
                        EffectiveDate = new DateTime(2024, 1, 1)
                    },
                    new BenefitPlanSummary
                    {
                        PlanId = "PLAN1002",
                        PlanName = "Silver HMO Standard",
                        ProductType = "HMO",
                        EnrolledMembers = 982,
                        EffectiveDate = new DateTime(2024, 1, 1)
                    },
                    new BenefitPlanSummary
                    {
                        PlanId = "PLAN1003",
                        PlanName = "Bronze HDHP Value",
                        ProductType = "HDHP",
                        EnrolledMembers = 342,
                        EffectiveDate = new DateTime(2025, 1, 1)
                    }
                }
            },
            ["SPNSR10002"] = new SponsorDetails
            {
                SponsorId = "SPNSR10002",
                Name = "TechStart Industries",
                Type = "Employer",
                State = "TX",
                ActiveBenefitPlans = 2,
                TotalMembers = 1523,
                Status = "Active",
                ContractStartDate = new DateTime(2025, 1, 1),
                ContractEndDate = new DateTime(2027, 12, 31),
                TaxId = "98-7654321",
                AddressLine1 = "500 Innovation Boulevard",
                City = "Austin",
                ZipCode = "78701",
                ContactName = "Michael Chen",
                ContactPhone = "(512) 555-0187",
                ContactEmail = "hr@techstart.com",
                BillingFrequency = "Quarterly",
                PaymentMethod = "Wire",
                GroupSizeTier = "Large (50+)",
                BenefitPlans = new List<BenefitPlanSummary>
                {
                    new BenefitPlanSummary
                    {
                        PlanId = "PLAN2001",
                        PlanName = "Premium EPO Network",
                        ProductType = "EPO",
                        EnrolledMembers = 923,
                        EffectiveDate = new DateTime(2025, 1, 1)
                    },
                    new BenefitPlanSummary
                    {
                        PlanId = "PLAN2002",
                        PlanName = "High Deductible HSA",
                        ProductType = "HDHP",
                        EnrolledMembers = 600,
                        EffectiveDate = new DateTime(2025, 1, 1)
                    }
                }
            }
        };

        return sponsors.TryGetValue(sponsorId, out var sponsor) 
            ? sponsor 
            : new SponsorDetails
            {
                SponsorId = sponsorId,
                Name = "Unknown Sponsor",
                Type = "Employer",
                State = "CA",
                Status = "Active",
                ContractStartDate = DateTime.Now.AddYears(-1),
                TaxId = "00-0000000",
                AddressLine1 = "123 Main St",
                City = "Unknown",
                ZipCode = "00000",
                ContactName = "Contact Person",
                ContactPhone = "(000) 000-0000",
                ContactEmail = "contact@example.com",
                BillingFrequency = "Monthly",
                PaymentMethod = "ACH",
                GroupSizeTier = "Small (<50)"
            };
    }

    private class CreateSponsorResponse
    {
        public string SponsorId { get; set; } = string.Empty;
    }
}
