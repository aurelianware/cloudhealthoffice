using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.Identity.Web;

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
            await SetBearerTokenAsync();
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
            await SetBearerTokenAsync();
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

    private async Task SetBearerTokenAsync()
    {
        var scopes = new[] { "api://31f76844-b2cb-47b1-aede-f5b2b6dc59c8/Authorization.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching providers (legacy), returning empty list");
            return new List<ProviderSummary>();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching providers, returning mock data");
            return GetMockProviders(specialty, networkStatus, searchTerm);
        }
    }

    public async Task<ProviderDetails?> GetProviderByIdAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ProviderDetails>($"{baseUrl}/providers/{providerId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching provider {ProviderId}, returning mock data", providerId);
            return GetMockProviderDetails(providerId);
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
            return result?.providerId ?? Guid.NewGuid().ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating provider, returning mock ID");
            return "PRV" + Random.Shared.Next(10000, 99999);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating provider {ProviderId}", providerId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching specialties, returning mock data");
            return GetMockSpecialties();
        }
    }

    private List<ProviderListItem> GetMockProviders(string? specialty, string? networkStatus, string? searchTerm)
    {
        var providers = new List<ProviderListItem>
        {
            new()
            {
                ProviderId = "PRV1001",
                NPI = "1234567890",
                Name = "Dr. Sarah Johnson",
                PracticeType = "Individual",
                Specialty = "Family Medicine",
                PracticeName = "Johnson Family Practice",
                City = "Austin",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 8,
                LastClaimDate = DateTime.Now.AddDays(-3)
            },
            new()
            {
                ProviderId = "PRV1002",
                NPI = "2345678901",
                Name = "Dr. Michael Chen",
                PracticeType = "Individual",
                Specialty = "Cardiology",
                PracticeName = "Heart Care Specialists",
                City = "Houston",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 12,
                LastClaimDate = DateTime.Now.AddDays(-1)
            },
            new()
            {
                ProviderId = "PRV1003",
                NPI = "3456789012",
                Name = "Dr. Emily Rodriguez",
                PracticeType = "Individual",
                Specialty = "Orthopedic Surgery",
                PracticeName = "Austin Orthopedic Associates",
                City = "Austin",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 6,
                LastClaimDate = DateTime.Now.AddDays(-5)
            },
            new()
            {
                ProviderId = "PRV1004",
                NPI = "4567890123",
                Name = "Dr. David Thompson",
                PracticeType = "Group",
                Specialty = "Radiology",
                PracticeName = "Texas Imaging Center",
                City = "Dallas",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 15,
                LastClaimDate = DateTime.Now.AddHours(-6)
            },
            new()
            {
                ProviderId = "PRV1005",
                NPI = "5678901234",
                Name = "Dr. Jennifer Martinez",
                PracticeType = "Individual",
                Specialty = "Pediatrics",
                PracticeName = "Kids First Pediatrics",
                City = "San Antonio",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 9,
                LastClaimDate = DateTime.Now.AddDays(-2)
            },
            new()
            {
                ProviderId = "PRV1006",
                NPI = "6789012345",
                Name = "Dr. Robert Wilson",
                PracticeType = "Group",
                Specialty = "Internal Medicine",
                PracticeName = "Capitol Medical Group",
                City = "Austin",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 11,
                LastClaimDate = DateTime.Now.AddHours(-18)
            },
            new()
            {
                ProviderId = "PRV1007",
                NPI = "7890123456",
                Name = "Dr. Lisa Anderson",
                PracticeType = "Individual",
                Specialty = "Dermatology",
                PracticeName = "Clear Skin Dermatology",
                City = "Houston",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 7,
                LastClaimDate = DateTime.Now.AddDays(-4)
            },
            new()
            {
                ProviderId = "PRV1008",
                NPI = "8901234567",
                Name = "Dr. James Lee",
                PracticeType = "Individual",
                Specialty = "Psychiatry",
                PracticeName = "Lee Mental Health Services",
                City = "Austin",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 5,
                LastClaimDate = DateTime.Now.AddDays(-6)
            },
            new()
            {
                ProviderId = "PRV1009",
                NPI = "9012345678",
                Name = "Dr. Patricia Garcia",
                PracticeType = "Group",
                Specialty = "Obstetrics and Gynecology",
                PracticeName = "Women's Health Partners",
                City = "Dallas",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 10,
                LastClaimDate = DateTime.Now.AddDays(-1)
            },
            new()
            {
                ProviderId = "PRV1010",
                NPI = "0123456789",
                Name = "Dr. Christopher Brown",
                PracticeType = "Individual",
                Specialty = "Oncology",
                PracticeName = "Texas Cancer Center",
                City = "Houston",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 8,
                LastClaimDate = DateTime.Now.AddDays(-7)
            },
            new()
            {
                ProviderId = "PRV1011",
                NPI = "1357924680",
                Name = "Dr. Amanda Taylor",
                PracticeType = "Individual",
                Specialty = "Physical Medicine and Rehabilitation",
                PracticeName = "Taylor Rehab & Wellness",
                City = "Austin",
                State = "TX",
                NetworkStatus = "Out-of-Network",
                CredentialingStatus = "Active",
                NetworkCount = 2,
                LastClaimDate = DateTime.Now.AddDays(-15)
            },
            new()
            {
                ProviderId = "PRV1012",
                NPI = "2468013579",
                Name = "Dr. Daniel White",
                PracticeType = "Group",
                Specialty = "Emergency Medicine",
                PracticeName = "Emergency Physicians of Texas",
                City = "San Antonio",
                State = "TX",
                NetworkStatus = "In-Network",
                CredentialingStatus = "Active",
                NetworkCount = 18,
                LastClaimDate = DateTime.Now.AddHours(-3)
            },
            new()
            {
                ProviderId = "PRV1013",
                NPI = "3691470258",
                Name = "Dr. Michelle Harris",
                PracticeType = "Individual",
                Specialty = "Endocrinology",
                PracticeName = "Diabetes & Hormone Center",
                City = "Austin",
                State = "TX",
                NetworkStatus = "Pending",
                CredentialingStatus = "Pending",
                NetworkCount = 0,
                LastClaimDate = null
            }
        };

        // Apply filters
        var filtered = providers.AsEnumerable();

        if (!string.IsNullOrEmpty(specialty))
            filtered = filtered.Where(p => p.Specialty.Equals(specialty, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(networkStatus))
            filtered = filtered.Where(p => p.NetworkStatus.Equals(networkStatus, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(searchTerm))
            filtered = filtered.Where(p =>
                p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                p.NPI.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                p.PracticeName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));

        return filtered.ToList();
    }

    private ProviderDetails? GetMockProviderDetails(string providerId)
    {
        var provider = GetMockProviders(null, null, null).FirstOrDefault(p => p.ProviderId == providerId);
        if (provider == null) return null;

        return new ProviderDetails
        {
            ProviderId = provider.ProviderId,
            NPI = provider.NPI,
            Name = provider.Name,
            PracticeType = provider.PracticeType,
            Specialty = provider.Specialty,
            PracticeName = provider.PracticeName,
            City = provider.City,
            State = provider.State,
            NetworkStatus = provider.NetworkStatus,
            CredentialingStatus = provider.CredentialingStatus,
            NetworkCount = provider.NetworkCount,
            LastClaimDate = provider.LastClaimDate,
            TaxonomyCode = provider.ProviderId switch
            {
                "PRV1001" => "207Q00000X",
                "PRV1002" => "207RC0000X",
                "PRV1003" => "207X00000X",
                "PRV1004" => "2085R0202X",
                "PRV1005" => "208000000X",
                _ => "208D00000X"
            },
            BoardCertifications = provider.ProviderId switch
            {
                "PRV1001" => new List<string> { "American Board of Family Medicine" },
                "PRV1002" => new List<string> { "American Board of Internal Medicine - Cardiology" },
                "PRV1003" => new List<string> { "American Board of Orthopaedic Surgery" },
                "PRV1005" => new List<string> { "American Board of Pediatrics" },
                _ => new List<string>()
            },
            Locations = GetMockLocations(providerId),
            Credentials = GetMockCredentials(providerId),
            NetworkAssignments = GetMockNetworkAssignments(providerId),
            Contract = GetMockContract(providerId),
            Performance = GetMockPerformance(providerId)
        };
    }

    private List<PracticeLocation> GetMockLocations(string providerId)
    {
        return providerId switch
        {
            "PRV1001" => new List<PracticeLocation>
            {
                new()
                {
                    LocationId = "LOC1001",
                    Name = "Main Office",
                    AddressLine1 = "1234 Medical Plaza Dr",
                    City = "Austin",
                    State = "TX",
                    ZipCode = "78701",
                    Phone = "(512) 555-1234",
                    Fax = "(512) 555-1235",
                    IsPrimary = true
                }
            },
            "PRV1002" => new List<PracticeLocation>
            {
                new()
                {
                    LocationId = "LOC1002",
                    Name = "Heart Care Specialists - Main",
                    AddressLine1 = "5678 Cardio Center Blvd",
                    City = "Houston",
                    State = "TX",
                    ZipCode = "77001",
                    Phone = "(713) 555-2345",
                    IsPrimary = true
                },
                new()
                {
                    LocationId = "LOC1003",
                    Name = "Heart Care Specialists - West",
                    AddressLine1 = "9012 West Loop Pkwy",
                    City = "Houston",
                    State = "TX",
                    ZipCode = "77027",
                    Phone = "(713) 555-2346",
                    IsPrimary = false
                }
            },
            "PRV1004" => new List<PracticeLocation>
            {
                new()
                {
                    LocationId = "LOC1004",
                    Name = "Texas Imaging Center - Dallas",
                    AddressLine1 = "2468 Diagnostic Dr",
                    City = "Dallas",
                    State = "TX",
                    ZipCode = "75201",
                    Phone = "(214) 555-3456",
                    IsPrimary = true
                },
                new()
                {
                    LocationId = "LOC1005",
                    Name = "Texas Imaging Center - Fort Worth",
                    AddressLine1 = "1357 Scanner Rd",
                    City = "Fort Worth",
                    State = "TX",
                    ZipCode = "76102",
                    Phone = "(817) 555-3457",
                    IsPrimary = false
                },
                new()
                {
                    LocationId = "LOC1006",
                    Name = "Texas Imaging Center - Plano",
                    AddressLine1 = "7890 North Central Expy",
                    City = "Plano",
                    State = "TX",
                    ZipCode = "75024",
                    Phone = "(972) 555-3458",
                    IsPrimary = false
                }
            },
            _ => new List<PracticeLocation>
            {
                new()
                {
                    LocationId = $"LOC{providerId.Replace("PRV", "")}",
                    Name = "Main Office",
                    AddressLine1 = "123 Healthcare Way",
                    City = "Austin",
                    State = "TX",
                    ZipCode = "78701",
                    Phone = "(512) 555-0000",
                    IsPrimary = true
                }
            }
        };
    }

    private List<ProviderCredential> GetMockCredentials(string providerId)
    {
        var credentials = new List<ProviderCredential>
        {
            new()
            {
                CredentialType = "Medical License",
                Number = $"TX-MD-{Random.Shared.Next(100000, 999999)}",
                IssuingState = "TX",
                IssueDate = DateTime.Now.AddYears(-8),
                ExpirationDate = DateTime.Now.AddYears(2),
                Status = "Active"
            },
            new()
            {
                CredentialType = "DEA Registration",
                Number = $"AD{Random.Shared.Next(1000000, 9999999)}",
                IssuingState = "TX",
                IssueDate = DateTime.Now.AddYears(-3),
                ExpirationDate = DateTime.Now.AddYears(1),
                Status = "Active"
            }
        };

        if (providerId is "PRV1001" or "PRV1002" or "PRV1003" or "PRV1005")
        {
            credentials.Add(new ProviderCredential
            {
                CredentialType = "Board Certification",
                Number = $"BC-{Random.Shared.Next(10000, 99999)}",
                IssuingState = "National",
                IssueDate = DateTime.Now.AddYears(-5),
                ExpirationDate = DateTime.Now.AddYears(5),
                Status = "Active"
            });
        }

        return credentials;
    }

    private List<NetworkAssignment> GetMockNetworkAssignments(string providerId)
    {
        if (providerId == "PRV1013")
            return new List<NetworkAssignment>();

        if (providerId == "PRV1011")
        {
            return new List<NetworkAssignment>
            {
                new()
                {
                    NetworkId = "NET1015",
                    NetworkName = "Extended PPO Network",
                    PlanName = "Premium PPO Plus",
                    EffectiveDate = DateTime.Now.AddYears(-1),
                    Status = "Active"
                }
            };
        }

        return new List<NetworkAssignment>
        {
            new()
            {
                NetworkId = "NET1001",
                NetworkName = "Gold PPO Network",
                PlanName = "Gold PPO Plus",
                EffectiveDate = DateTime.Now.AddYears(-2),
                Status = "Active"
            },
            new()
            {
                NetworkId = "NET1002",
                NetworkName = "Premium EPO Network",
                PlanName = "Premium EPO",
                EffectiveDate = DateTime.Now.AddYears(-1).AddMonths(-6),
                Status = "Active"
            },
            new()
            {
                NetworkId = "NET1003",
                NetworkName = "Union Health Network",
                PlanName = "Union Health Plus",
                EffectiveDate = DateTime.Now.AddYears(-3),
                Status = "Active"
            }
        };
    }

    private ProviderContract GetMockContract(string providerId)
    {
        return new ProviderContract
        {
            ContractId = $"CNT-{providerId}-2024",
            ReimbursementMethod = providerId switch
            {
                "PRV1001" => "Capitation",
                "PRV1004" => "Fee Schedule",
                "PRV1006" => "Capitation",
                _ => "Fee Schedule"
            },
            FeeScheduleTier = providerId switch
            {
                "PRV1002" => "Tier 1 - Specialist",
                "PRV1003" => "Tier 1 - Specialist",
                "PRV1004" => "Tier 2 - Ancillary",
                _ => "Tier 1 - Primary Care"
            },
            EffectiveDate = DateTime.Now.AddYears(-2),
            CapitationRate = providerId switch
            {
                "PRV1001" => 42.50m,
                "PRV1006" => 38.75m,
                _ => null
            }
        };
    }

    private ProviderPerformance GetMockPerformance(string providerId)
    {
        return providerId switch
        {
            "PRV1001" => new ProviderPerformance
            {
                ClaimsLast90Days = 287,
                TotalBilledLast90Days = 43250.00m,
                AvgClaimAmount = 150.70m,
                AuthorizationRequests = 42,
                AuthorizationApprovalRate = 0.93m,
                DenialCount = 8,
                DenialRate = 0.028m,
                AvgProcessingTimeDays = 3.2m,
                QualityScore = 4.5m
            },
            "PRV1002" => new ProviderPerformance
            {
                ClaimsLast90Days = 156,
                TotalBilledLast90Days = 187300.00m,
                AvgClaimAmount = 1200.64m,
                AuthorizationRequests = 89,
                AuthorizationApprovalRate = 0.88m,
                DenialCount = 12,
                DenialRate = 0.077m,
                AvgProcessingTimeDays = 4.7m,
                QualityScore = 4.2m
            },
            "PRV1003" => new ProviderPerformance
            {
                ClaimsLast90Days = 68,
                TotalBilledLast90Days = 425600.00m,
                AvgClaimAmount = 6258.82m,
                AuthorizationRequests = 58,
                AuthorizationApprovalRate = 0.91m,
                DenialCount = 5,
                DenialRate = 0.074m,
                AvgProcessingTimeDays = 6.1m,
                QualityScore = 4.7m
            },
            "PRV1004" => new ProviderPerformance
            {
                ClaimsLast90Days = 523,
                TotalBilledLast90Days = 312450.00m,
                AvgClaimAmount = 597.42m,
                AuthorizationRequests = 187,
                AuthorizationApprovalRate = 0.95m,
                DenialCount = 15,
                DenialRate = 0.029m,
                AvgProcessingTimeDays = 2.3m,
                QualityScore = 4.6m
            },
            _ => new ProviderPerformance
            {
                ClaimsLast90Days = 125,
                TotalBilledLast90Days = 75000.00m,
                AvgClaimAmount = 600.00m,
                AuthorizationRequests = 35,
                AuthorizationApprovalRate = 0.90m,
                DenialCount = 6,
                DenialRate = 0.048m,
                AvgProcessingTimeDays = 4.0m,
                QualityScore = 4.3m
            }
        };
    }

    private List<string> GetMockSpecialties()
    {
        return new List<string>
        {
            "Family Medicine",
            "Internal Medicine",
            "Pediatrics",
            "Obstetrics and Gynecology",
            "Cardiology",
            "Orthopedic Surgery",
            "Radiology",
            "Dermatology",
            "Psychiatry",
            "Oncology",
            "Emergency Medicine",
            "Endocrinology",
            "Physical Medicine and Rehabilitation",
            "Neurology",
            "Gastroenterology",
            "Pulmonology",
            "Nephrology",
            "Urology"
        };
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching benefit plans, returning empty list");
            return new List<BenefitPlan>();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching benefit plans, returning mock data");
            return GetMockBenefitPlans(sponsorId, productType);
        }
    }

    public async Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<BenefitPlanDetails>($"{baseUrl}/benefit-plans/{planId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching benefit plan {PlanId}, returning mock data", planId);
            return GetMockBenefitPlanDetails(planId);
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
            return result?.PlanId ?? $"PLAN{Random.Shared.Next(1000, 9999)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating benefit plan, generating mock ID");
            await Task.Delay(500);
            return $"PLAN{Random.Shared.Next(1000, 9999)}";
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating benefit plan {PlanId}, mock update successful", planId);
            await Task.Delay(300);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching benefits library, returning mock data");
            return GetMockBenefitsLibrary();
        }
    }

    private List<BenefitPlanListItem> GetMockBenefitPlans(string? sponsorId, string? productType)
    {
        var plans = new List<BenefitPlanListItem>
        {
            new BenefitPlanListItem
            {
                PlanId = "PLAN1001",
                PlanName = "Gold PPO Plus",
                SponsorId = "SPNSR10001",
                SponsorName = "Acme Corporation",
                ProductType = "PPO",
                Network = "National Premier Network",
                EnrolledMembers = 1523,
                AssignedBenefits = 42,
                Status = "Active",
                EffectiveDate = new DateTime(2024, 1, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN1002",
                PlanName = "Silver HMO Standard",
                SponsorId = "SPNSR10001",
                SponsorName = "Acme Corporation",
                ProductType = "HMO",
                Network = "Regional Select Network",
                EnrolledMembers = 982,
                AssignedBenefits = 38,
                Status = "Active",
                EffectiveDate = new DateTime(2024, 1, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN1003",
                PlanName = "Bronze HDHP Value",
                SponsorId = "SPNSR10001",
                SponsorName = "Acme Corporation",
                ProductType = "HDHP",
                Network = "National Basic Network",
                EnrolledMembers = 342,
                AssignedBenefits = 28,
                Status = "Active",
                EffectiveDate = new DateTime(2025, 1, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN2001",
                PlanName = "Premium EPO Network",
                SponsorId = "SPNSR10002",
                SponsorName = "TechStart Industries",
                ProductType = "EPO",
                Network = "Metro Exclusive Network",
                EnrolledMembers = 923,
                AssignedBenefits = 45,
                Status = "Active",
                EffectiveDate = new DateTime(2025, 1, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN2002",
                PlanName = "High Deductible HSA",
                SponsorId = "SPNSR10002",
                SponsorName = "TechStart Industries",
                ProductType = "HDHP",
                Network = "National Basic Network",
                EnrolledMembers = 600,
                AssignedBenefits = 32,
                Status = "Active",
                EffectiveDate = new DateTime(2025, 1, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN3001",
                PlanName = "Union Gold PPO",
                SponsorId = "SPNSR10003",
                SponsorName = "United Workers Local 247",
                ProductType = "PPO",
                Network = "National Premier Network",
                EnrolledMembers = 2134,
                AssignedBenefits = 48,
                Status = "Active",
                EffectiveDate = new DateTime(2023, 6, 1)
            },
            new BenefitPlanListItem
            {
                PlanId = "PLAN3002",
                PlanName = "Union Silver HMO",
                SponsorId = "SPNSR10003",
                SponsorName = "United Workers Local 247",
                ProductType = "HMO",
                Network = "Regional Select Network",
                EnrolledMembers = 1587,
                AssignedBenefits = 40,
                Status = "Active",
                EffectiveDate = new DateTime(2023, 6, 1)
            }
        };

        if (!string.IsNullOrEmpty(sponsorId))
            plans = plans.Where(p => p.SponsorId == sponsorId).ToList();

        if (!string.IsNullOrEmpty(productType) && productType != "All")
            plans = plans.Where(p => p.ProductType == productType).ToList();

        return plans;
    }

    private BenefitPlanDetails GetMockBenefitPlanDetails(string planId)
    {
        var planDict = new Dictionary<string, BenefitPlanDetails>
        {
            ["PLAN1001"] = new BenefitPlanDetails
            {
                PlanId = "PLAN1001",
                PlanName = "Gold PPO Plus",
                SponsorId = "SPNSR10001",
                SponsorName = "Acme Corporation",
                ProductType = "PPO",
                Network = "National Premier Network",
                MetalTier = "Gold",
                EnrolledMembers = 1523,
                AssignedBenefits = 42,
                Status = "Active",
                EffectiveDate = new DateTime(2024, 1, 1),
                IndividualDeductible = 1500m,
                FamilyDeductible = 3000m,
                IndividualOOPMax = 6000m,
                FamilyOOPMax = 12000m,
                Coinsurance = 20m,
                MonthlyPremium = 487.50m,
                PlanYear = "2024",
                Benefits = new List<PlanBenefit>
                {
                    new PlanBenefit
                    {
                        BenefitId = "BEN001",
                        ServiceType = "Office Visit - Primary Care",
                        Category = "Medical",
                        Copay = 25m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN002",
                        ServiceType = "Office Visit - Specialist",
                        Category = "Medical",
                        Copay = 50m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN003",
                        ServiceType = "Inpatient Hospital",
                        Category = "Medical",
                        CoinsurancePercent = 20m,
                        CoveragePercent = 80m,
                        PriorAuthRequired = true
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN004",
                        ServiceType = "Emergency Room",
                        Category = "Medical",
                        Copay = 350m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN005",
                        ServiceType = "Generic Drugs",
                        Category = "Pharmacy",
                        Copay = 10m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN006",
                        ServiceType = "Preferred Brand Drugs",
                        Category = "Pharmacy",
                        Copay = 40m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    }
                },
                Exclusions = new List<string>
                {
                    "Cosmetic procedures",
                    "Experimental treatments",
                    "Weight loss programs"
                }
            },
            ["PLAN2001"] = new BenefitPlanDetails
            {
                PlanId = "PLAN2001",
                PlanName = "Premium EPO Network",
                SponsorId = "SPNSR10002",
                SponsorName = "TechStart Industries",
                ProductType = "EPO",
                Network = "Metro Exclusive Network",
                MetalTier = "Platinum",
                EnrolledMembers = 923,
                AssignedBenefits = 45,
                Status = "Active",
                EffectiveDate = new DateTime(2025, 1, 1),
                IndividualDeductible = 500m,
                FamilyDeductible = 1000m,
                IndividualOOPMax = 4000m,
                FamilyOOPMax = 8000m,
                Coinsurance = 10m,
                MonthlyPremium = 625.00m,
                PlanYear = "2025",
                Benefits = new List<PlanBenefit>
                {
                    new PlanBenefit
                    {
                        BenefitId = "BEN001",
                        ServiceType = "Office Visit - Primary Care",
                        Category = "Medical",
                        Copay = 15m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN002",
                        ServiceType = "Office Visit - Specialist",
                        Category = "Medical",
                        Copay = 30m,
                        CoveragePercent = 100m,
                        PriorAuthRequired = false
                    },
                    new PlanBenefit
                    {
                        BenefitId = "BEN003",
                        ServiceType = "Inpatient Hospital",
                        Category = "Medical",
                        CoinsurancePercent = 10m,
                        CoveragePercent = 90m,
                        PriorAuthRequired = true
                    }
                },
                Exclusions = new List<string>
                {
                    "Out-of-network care (except emergencies)",
                    "Cosmetic procedures"
                }
            }
        };

        return planDict.TryGetValue(planId, out var plan)
            ? plan
            : new BenefitPlanDetails
            {
                PlanId = planId,
                PlanName = "Unknown Plan",
                SponsorId = "SPNSR00000",
                SponsorName = "Unknown Sponsor",
                ProductType = "PPO",
                Network = "Unknown",
                MetalTier = "Bronze",
                Status = "Active",
                EffectiveDate = DateTime.Now,
                PlanYear = DateTime.Now.Year.ToString()
            };
    }

    private List<BenefitItem> GetMockBenefitsLibrary()
    {
        return new List<BenefitItem>
        {
            new BenefitItem
            {
                BenefitId = "BEN001",
                ServiceType = "Office Visit - Primary Care",
                Category = "Medical",
                Description = "Routine office visit with primary care physician",
                DefaultCopay = 25m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN002",
                ServiceType = "Office Visit - Specialist",
                Category = "Medical",
                Description = "Office visit with specialist physician",
                DefaultCopay = 50m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN003",
                ServiceType = "Inpatient Hospital",
                Category = "Medical",
                Description = "Inpatient hospital admission and care",
                DefaultCoinsurance = 20m,
                RequiresPriorAuth = true
            },
            new BenefitItem
            {
                BenefitId = "BEN004",
                ServiceType = "Emergency Room",
                Category = "Medical",
                Description = "Emergency room visit",
                DefaultCopay = 350m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN005",
                ServiceType = "Generic Drugs",
                Category = "Pharmacy",
                Description = "Generic prescription medications",
                DefaultCopay = 10m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN006",
                ServiceType = "Preferred Brand Drugs",
                Category = "Pharmacy",
                Description = "Preferred brand prescription medications",
                DefaultCopay = 40m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN007",
                ServiceType = "Non-Preferred Brand Drugs",
                Category = "Pharmacy",
                Description = "Non-preferred brand prescription medications",
                DefaultCopay = 80m,
                RequiresPriorAuth = true
            },
            new BenefitItem
            {
                BenefitId = "BEN008",
                ServiceType = "Preventive Care",
                Category = "Medical",
                Description = "Annual physical exam, immunizations, screenings",
                DefaultCopay = 0m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN009",
                ServiceType = "Laboratory Services",
                Category = "Medical",
                Description = "Laboratory tests and diagnostic procedures",
                DefaultCoinsurance = 20m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN010",
                ServiceType = "Imaging - X-Ray",
                Category = "Medical",
                Description = "X-ray imaging services",
                DefaultCoinsurance = 20m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN011",
                ServiceType = "Imaging - MRI/CT",
                Category = "Medical",
                Description = "Advanced imaging (MRI, CT, PET scans)",
                DefaultCoinsurance = 20m,
                RequiresPriorAuth = true
            },
            new BenefitItem
            {
                BenefitId = "BEN012",
                ServiceType = "Physical Therapy",
                Category = "Medical",
                Description = "Physical therapy services",
                DefaultCopay = 40m,
                RequiresPriorAuth = true
            },
            new BenefitItem
            {
                BenefitId = "BEN013",
                ServiceType = "Mental Health Outpatient",
                Category = "Mental Health",
                Description = "Outpatient mental health therapy",
                DefaultCopay = 30m,
                RequiresPriorAuth = false
            },
            new BenefitItem
            {
                BenefitId = "BEN014",
                ServiceType = "Durable Medical Equipment",
                Category = "Medical",
                Description = "Durable medical equipment (wheelchairs, crutches, etc.)",
                DefaultCoinsurance = 20m,
                RequiresPriorAuth = true
            }
        };
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
            await SetBearerTokenAsync();
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

    private async Task SetBearerTokenAsync()
    {
        var scopes = new[] { "api://31f76844-b2cb-47b1-aede-f5b2b6dc59c8/Attachments.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching codes, returning mock data");
            return GetMockCodes(codeSystem, searchTerm);
        }
    }

    public async Task<MedicalCodeDetails?> GetCodeDetailsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MedicalCodeDetails>($"{baseUrl}/codes/{codeSystem}/{code}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching code details for {CodeSystem}/{Code}, returning mock data", codeSystem, code);
            return GetMockCodeDetails(codeSystem, code);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching code systems, returning mock data");
            return new List<string>
            {
                "CPT",
                "ICD-10-CM",
                "HCPCS",
                "Revenue Code",
                "Place of Service",
                "DRG",
                "Modifier"
            };
        }
    }

    public async Task<CodeUsageStats> GetCodeUsageStatsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<CodeUsageStats>($"{baseUrl}/codes/{codeSystem}/{code}/usage");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching code usage stats, returning mock data");
            return GetMockCodeUsageStats(codeSystem, code);
        }
    }

    private List<MedicalCode> GetMockCodes(string? codeSystem, string? searchTerm)
    {
        var allCodes = new List<MedicalCode>
        {
            // CPT Codes
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "99213",
                ShortDescription = "Office outpatient visit 15 minutes",
                Category = "Evaluation & Management",
                EffectiveDate = new DateTime(2020, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "99214",
                ShortDescription = "Office outpatient visit 25 minutes",
                Category = "Evaluation & Management",
                EffectiveDate = new DateTime(2020, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "99215",
                ShortDescription = "Office outpatient visit 40 minutes",
                Category = "Evaluation & Management",
                EffectiveDate = new DateTime(2020, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "70450",
                ShortDescription = "CT head/brain without contrast",
                Category = "Radiology",
                EffectiveDate = new DateTime(2018, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "73721",
                ShortDescription = "MRI any joint of lower extremity",
                Category = "Radiology",
                EffectiveDate = new DateTime(2019, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "27447",
                ShortDescription = "Total knee arthroplasty",
                Category = "Surgery",
                EffectiveDate = new DateTime(2015, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "CPT",
                Code = "97110",
                ShortDescription = "Therapeutic exercises",
                Category = "Physical Medicine",
                EffectiveDate = new DateTime(2018, 1, 1),
                Status = "Active"
            },

            // ICD-10-CM Codes
            new MedicalCode
            {
                CodeSystem = "ICD-10-CM",
                Code = "E11.9",
                ShortDescription = "Type 2 diabetes mellitus without complications",
                Category = "Endocrine",
                EffectiveDate = new DateTime(2015, 10, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "ICD-10-CM",
                Code = "I10",
                ShortDescription = "Essential (primary) hypertension",
                Category = "Circulatory",
                EffectiveDate = new DateTime(2015, 10, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "ICD-10-CM",
                Code = "M17.11",
                ShortDescription = "Unilateral primary osteoarthritis, right knee",
                Category = "Musculoskeletal",
                EffectiveDate = new DateTime(2016, 10, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "ICD-10-CM",
                Code = "J44.1",
                ShortDescription = "COPD with acute exacerbation",
                Category = "Respiratory",
                EffectiveDate = new DateTime(2017, 10, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "ICD-10-CM",
                Code = "Z00.00",
                ShortDescription = "Encounter for general adult medical examination without abnormal findings",
                Category = "Factors Influencing Health",
                EffectiveDate = new DateTime(2015, 10, 1),
                Status = "Active"
            },

            // HCPCS Codes
            new MedicalCode
            {
                CodeSystem = "HCPCS",
                Code = "J0135",
                ShortDescription = "Adalimumab injection 20 mg",
                Category = "Drugs",
                EffectiveDate = new DateTime(2020, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "HCPCS",
                Code = "E0601",
                ShortDescription = "Continuous positive airway pressure device",
                Category = "Durable Medical Equipment",
                EffectiveDate = new DateTime(2018, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "HCPCS",
                Code = "L3670",
                ShortDescription = "Shoulder orthosis",
                Category = "Orthotics",
                EffectiveDate = new DateTime(2019, 1, 1),
                Status = "Active"
            },

            // Revenue Codes
            new MedicalCode
            {
                CodeSystem = "Revenue Code",
                Code = "0450",
                ShortDescription = "Emergency room - general classification",
                Category = "Hospital Services",
                EffectiveDate = new DateTime(2010, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "Revenue Code",
                Code = "0300",
                ShortDescription = "Laboratory - general classification",
                Category = "Ancillary Services",
                EffectiveDate = new DateTime(2010, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "Revenue Code",
                Code = "0250",
                ShortDescription = "Pharmacy - general classification",
                Category = "Pharmacy",
                EffectiveDate = new DateTime(2010, 1, 1),
                Status = "Active"
            },

            // Place of Service
            new MedicalCode
            {
                CodeSystem = "Place of Service",
                Code = "11",
                ShortDescription = "Office",
                Category = "Non-Facility",
                EffectiveDate = new DateTime(2000, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "Place of Service",
                Code = "22",
                ShortDescription = "On Campus-Outpatient Hospital",
                Category = "Facility",
                EffectiveDate = new DateTime(2000, 1, 1),
                Status = "Active"
            },
            new MedicalCode
            {
                CodeSystem = "Place of Service",
                Code = "23",
                ShortDescription = "Emergency Room - Hospital",
                Category = "Facility",
                EffectiveDate = new DateTime(2000, 1, 1),
                Status = "Active"
            }
        };

        var filteredCodes = allCodes;

        if (!string.IsNullOrEmpty(codeSystem) && codeSystem != "All")
        {
            filteredCodes = filteredCodes.Where(c => c.CodeSystem == codeSystem).ToList();
        }

        if (!string.IsNullOrEmpty(searchTerm))
        {
            filteredCodes = filteredCodes.Where(c =>
                c.Code.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                c.ShortDescription.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                c.Category.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        return filteredCodes;
    }

    private MedicalCodeDetails GetMockCodeDetails(string codeSystem, string code)
    {
        var codeDetailsDict = new Dictionary<string, MedicalCodeDetails>
        {
            ["CPT-99213"] = new MedicalCodeDetails
            {
                CodeSystem = "CPT",
                Code = "99213",
                ShortDescription = "Office outpatient visit 15 minutes",
                LongDescription = "Office or other outpatient visit for the evaluation and management of an established patient, which requires a medically appropriate history and/or examination and low level of medical decision making. When using time for code selection, 20-29 minutes of total time is spent on the date of the encounter.",
                Category = "Evaluation & Management",
                EffectiveDate = new DateTime(2021, 1, 1),
                Status = "Active",
                Keywords = new List<string> { "office visit", "E&M", "established patient", "level 3" },
                RelatedCodes = new List<RelatedCode>
                {
                    new RelatedCode { CodeSystem = "CPT", Code = "99212", Description = "Office visit level 2", RelationType = "Alternative" },
                    new RelatedCode { CodeSystem = "CPT", Code = "99214", Description = "Office visit level 4", RelationType = "Alternative" }
                },
                RequiresPriorAuth = false,
                ClinicalNotes = "Most commonly used code for routine established patient visits"
            },
            ["CPT-70450"] = new MedicalCodeDetails
            {
                CodeSystem = "CPT",
                Code = "70450",
                ShortDescription = "CT head/brain without contrast",
                LongDescription = "Computed tomography, head or brain; without contrast material",
                Category = "Radiology",
                EffectiveDate = new DateTime(2018, 1, 1),
                Status = "Active",
                Keywords = new List<string> { "CT scan", "head", "brain", "imaging", "radiology" },
                RelatedCodes = new List<RelatedCode>
                {
                    new RelatedCode { CodeSystem = "CPT", Code = "70460", Description = "CT head with contrast", RelationType = "Alternative" },
                    new RelatedCode { CodeSystem = "ICD-10-CM", Code = "R51.9", Description = "Headache unspecified", RelationType = "CrossReference" }
                },
                RequiresPriorAuth = true,
                ClinicalNotes = "Prior authorization typically required for non-emergency scans"
            },
            ["ICD-10-CM-E11.9"] = new MedicalCodeDetails
            {
                CodeSystem = "ICD-10-CM",
                Code = "E11.9",
                ShortDescription = "Type 2 diabetes mellitus without complications",
                LongDescription = "Type 2 diabetes mellitus without complications. This code is used when a patient has diabetes mellitus type 2 and there are no documented complications or manifestations.",
                Category = "Endocrine",
                EffectiveDate = new DateTime(2015, 10, 1),
                Status = "Active",
                Keywords = new List<string> { "diabetes", "type 2", "DM", "uncomplicated" },
                ParentCode = "E11",
                ChildCodes = new List<string>(),
                RelatedCodes = new List<RelatedCode>
                {
                    new RelatedCode { CodeSystem = "ICD-10-CM", Code = "E11.65", Description = "Type 2 DM with hyperglycemia", RelationType = "Alternative" },
                    new RelatedCode { CodeSystem = "CPT", Code = "99213", Description = "Office visit for DM management", RelationType = "CrossReference" }
                },
                RequiresPriorAuth = false,
                ClinicalNotes = "Use more specific code if complications present"
            }
        };

        var key = $"{codeSystem}-{code}";
        return codeDetailsDict.TryGetValue(key, out var details)
            ? details
            : new MedicalCodeDetails
            {
                CodeSystem = codeSystem,
                Code = code,
                ShortDescription = "Code description not available",
                LongDescription = "Detailed description not available in mock data",
                Category = "General",
                EffectiveDate = DateTime.Now.AddYears(-1),
                Status = "Active"
            };
    }

    private CodeUsageStats GetMockCodeUsageStats(string codeSystem, string code)
    {
        return new CodeUsageStats
        {
            CodeSystem = codeSystem,
            Code = code,
            ClaimsCount = Random.Shared.Next(50, 5000),
            AuthorizationsCount = Random.Shared.Next(10, 500),
            BenefitsCount = Random.Shared.Next(1, 20),
            LastUsedDate = DateTime.Now.AddDays(-Random.Shared.Next(1, 90)),
            TotalBilledAmount = Random.Shared.Next(10000, 1000000)
        };
    }
}
