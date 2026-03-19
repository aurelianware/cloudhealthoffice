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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching claims, returning mock data");
            return GetMockSearchResults(request);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating claim status for claim {ClaimId}", claimId);
            throw;
        }
    }

    private ClaimSearchResult GetMockSearchResults(ClaimSearchRequest request)
    {
        var allClaims = GetMockClaims(100);
        var filteredClaims = allClaims.AsEnumerable();

        // Apply filters
        if (!string.IsNullOrEmpty(request.ClaimNumber))
            filteredClaims = filteredClaims.Where(c => c.ClaimId.Contains(request.ClaimNumber, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(request.MemberId))
            filteredClaims = filteredClaims.Where(c => c.MemberId == request.MemberId);

        if (!string.IsNullOrEmpty(request.Status))
            filteredClaims = filteredClaims.Where(c => c.Status == request.Status);

        // Apply sorting
        if (request.SortOrder == "Ascending")
        {
            filteredClaims = request.SortBy switch
            {
                "ServiceDate" => filteredClaims.OrderBy(c => c.ServiceDateFrom),
                "Amount" => filteredClaims.OrderBy(c => c.TotalChargeAmount),
                "Status" => filteredClaims.OrderBy(c => c.Status),
                _ => filteredClaims.OrderBy(c => c.SubmittedDate)
            };
        }
        else
        {
            filteredClaims = request.SortBy switch
            {
                "ServiceDate" => filteredClaims.OrderByDescending(c => c.ServiceDateFrom),
                "Amount" => filteredClaims.OrderByDescending(c => c.TotalChargeAmount),
                "Status" => filteredClaims.OrderByDescending(c => c.Status),
                _ => filteredClaims.OrderByDescending(c => c.SubmittedDate)
            };
        }

        // Apply pagination
        var claimsList = filteredClaims.ToList();
        var totalCount = claimsList.Count;
        var pageSize = request.PageSize;
        var pageNumber = Math.Max(1, request.PageNumber);
        var skip = (pageNumber - 1) * pageSize;
        var pageItems = claimsList.Skip(skip).Take(pageSize).ToList();

        return new ClaimSearchResult
        {
            Claims = pageItems,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalChargeAmount = claimsList.Sum(c => c.TotalChargeAmount),
            TotalAllowedAmount = claimsList.Sum(c => c.AllowedAmount),
            TotalPaidAmount = claimsList.Sum(c => c.PaidAmount),
            ApprovedCount = claimsList.Count(c => c.Status == "Approved" || c.Status == "Paid" || c.Status == "PartiallyPaid"),
            DeniedCount = claimsList.Count(c => c.Status == "Denied"),
            PendingCount = claimsList.Count(c => c.Status == "Pended" || c.Status == "InAdjudication")
        };
    }

    private List<ClaimSummary> GetMockClaims(int count)
    {
        var random = Random.Shared;
        var statuses = new[] { "Approved", "Approved", "Approved", "Denied", "Pended", "InAdjudication" };
        var claimTypes = new[] { "Professional", "Professional", "Institutional" };
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
        for (int i = 1; i <= Math.Min(count, 100); i++)
        {
            var member = members[Random.Shared.Next(members.Length)];
            var provider = providers[Random.Shared.Next(providers.Length)];
            var status = statuses[Random.Shared.Next(statuses.Length)];
            var claimType = claimTypes[Random.Shared.Next(claimTypes.Length)];
            var chargeAmount = (decimal)Random.Shared.Next(500, 50000);
            var allowedAmount = chargeAmount * 0.85m;
            var paidAmount = status == "Approved" || status == "Paid" ? allowedAmount * 0.8m : 0m;
            var serviceDate = DateTime.Now.AddDays(-Random.Shared.Next(1, 90));
            
            claims.Add(new ClaimSummary
            {
                ClaimId = $"CLM-2026-{i:D5}",
                ClaimNumber = $"CLM{i:D8}",
                MemberId = member.Item1,
                MemberName = member.Item2,
                ProviderId = provider.Item1,
                ProviderName = provider.Item2,
                ClaimType = claimType,
                TotalChargeAmount = chargeAmount,
                AllowedAmount = allowedAmount,
                PaidAmount = paidAmount,
                Status = status,
                ServiceDateFrom = serviceDate,
                ServiceDateTo = serviceDate.AddDays(1),
                SubmittedDate = serviceDate.AddDays(1),
                AdjudicatedDate = status != "Submitted" && status != "Received" ? serviceDate.AddDays(3) : null,
                ProcessingTimeMs = Random.Shared.Next(150, 800),
                LineCount = Random.Shared.Next(1, 5)
            });
        }

        return claims.OrderByDescending(c => c.SubmittedDate).ToList();
    }

    private ClaimDetails GetMockClaimDetails(string claimId)
    {
        var random = new Random(claimId.GetHashCode());
        var statuses = new[] { "Approved", "Denied", "Pended", "InAdjudication" };
        var claimTypes = new[] { "Professional", "Institutional" };
        var status = statuses[random.Next(statuses.Length)];
        var claimType = claimTypes[random.Next(claimTypes.Length)];

        var serviceLines = new List<ClaimServiceLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ProcedureDescription = "Office Visit - Established Patient, Level 3",
                ChargeAmount = 150.00m,
                AllowedAmount = 125.00m,
                PaidAmount = 100.00m,
                PatientResponsibility = 25.00m,
                Units = 1,
                ServiceDateFrom = DateTime.Now.AddDays(-15),
                ServiceDateTo = DateTime.Now.AddDays(-15),
                Modifiers = new() { "76", "77" },
                LineStatus = status,
                DiagnosisPointers = new() { 1, 2 }
            },
            new()
            {
                LineNumber = 2,
                ProcedureCode = "80053",
                ProcedureDescription = "Comprehensive Metabolic Panel",
                ChargeAmount = 85.00m,
                AllowedAmount = 75.00m,
                PaidAmount = 60.00m,
                PatientResponsibility = 15.00m,
                Units = 1,
                ServiceDateFrom = DateTime.Now.AddDays(-15),
                ServiceDateTo = DateTime.Now.AddDays(-15),
                LineStatus = status,
                DiagnosisPointers = new() { 1 },
                Adjustments = status == "Denied" || status == "Pended" ? new()
                {
                    new()
                    {
                        GroupCode = "CO",
                        ReasonCode = "45",
                        Amount = -15.00m,
                        Description = "Late filing - exceeds 90 day limit"
                    }
                } : new()
            },
            new()
            {
                LineNumber = 3,
                ProcedureCode = "85025",
                ProcedureDescription = "Complete Blood Count with differential",
                ChargeAmount = 45.00m,
                AllowedAmount = 40.00m,
                PaidAmount = 32.00m,
                PatientResponsibility = 8.00m,
                Units = 1,
                ServiceDateFrom = DateTime.Now.AddDays(-15),
                ServiceDateTo = DateTime.Now.AddDays(-15),
                LineStatus = status,
                DiagnosisPointers = new() { 1 }
            }
        };

        var diagnosisCodes = new List<ClaimDiagnosisCode>
        {
            new()
            {
                Code = "E11.9",
                Description = "Type 2 diabetes mellitus without complications",
                Type = "Principal",
                PointerNumber = 1
            },
            new()
            {
                Code = "I10",
                Description = "Essential (primary) hypertension",
                Type = "Secondary",
                PointerNumber = 2
            },
            new()
            {
                Code = "Z79.4",
                Description = "Long term (current) use of insulin",
                Type = "Secondary",
                PointerNumber = 3
            }
        };

        var totalCharge = serviceLines.Sum(sl => sl.ChargeAmount);
        var totalAllowed = serviceLines.Sum(sl => sl.AllowedAmount);
        var totalPaid = status == "Approved" || status == "Paid" ? serviceLines.Sum(sl => sl.PaidAmount) : 0;
        var patientResp = status == "Approved" || status == "Paid" ? serviceLines.Sum(sl => sl.PatientResponsibility) : totalCharge;

        var auditTrail = new List<ClaimAudit>
        {
            new()
            {
                Timestamp = DateTime.Now.AddDays(-10),
                Action = "Claim received",
                ChangedBy = "System",
                Notes = "EDI 837 transaction received"
            },
            new()
            {
                Timestamp = DateTime.Now.AddDays(-8),
                Action = "Status changed",
                ChangedBy = "system-adjudication",
                OldValue = "Received",
                NewValue = "InAdjudication",
                Notes = "Automatic adjudication workflow triggered"
            }
        };

        if (status == "Approved" || status == "Denied" || status == "Pended")
        {
            auditTrail.Add(new()
            {
                Timestamp = DateTime.Now.AddDays(-2),
                Action = "Claim adjudicated",
                ChangedBy = "claims-examiner-001",
                OldValue = "InAdjudication",
                NewValue = status,
                Notes = status == "Approved" ? "Claim approved" : status == "Denied" ? "Claim denied - insufficient documentation" : "Sent to pending review"
            });
        }

        var claimNum = int.Parse(claimId.Replace("CLM-2026-", ""));

        return new ClaimDetails
        {
            ClaimId = claimId,
            ClaimNumber = $"CLM{claimNum:D8}",
            MemberId = "MBR-2024-001",
            MemberName = "Sarah Johnson",
            SubscriberId = "MBR-2024-001",
            SubscriberName = "Sarah Johnson",
            PatientName = "Sarah Johnson",
            PatientRelationship = "Self",
            ProviderId = "PRV-001",
            ProviderName = "Seattle Medical Center",
            BillingProviderName = "Seattle Medical Center",
            BillingProviderNPI = "1234567890",
            RenderingProviderName = "Dr. James Smith",
            RenderingProviderNPI = "1234567891",
            FacilityName = "Seattle Medical Center Outpatient",
            FacilityNPI = "1234567892",
            PlaceOfService = "11 - Office",
            ClaimType = claimType,
            TotalChargeAmount = totalCharge,
            AllowedAmount = totalAllowed,
            PaidAmount = totalPaid,
            DeductibleAmount = 0.00m,
            CoinsuranceAmount = 15.00m,
            CopayAmount = 25.00m,
            PatientResponsibility = patientResp,
            Status = status,
            ProcessingTimeMs = random.Next(200, 600),
            ServiceDateFrom = DateTime.Now.AddDays(-15),
            ServiceDateTo = DateTime.Now.AddDays(-15),
            SubmittedDate = DateTime.Now.AddDays(-10),
            ReceivedDate = DateTime.Now.AddDays(-10),
            AdjudicatedDate = status != "Submitted" && status != "Received" ? DateTime.Now.AddDays(-2) : null,
            PaidDate = status == "Paid" ? DateTime.Now.AddDays(-1) : null,
            CheckNumber = status == "Paid" ? "CHK123456" : null,
            PriorAuthorizationNumber = "AUTH-2024-78901",
            ReferralNumber = "REF-2024-5467",
            ClaimNotes = "Routine follow-up visit for chronic disease management",
            DenialReason = status == "Denied" ? "Service not covered" : null,
            DiagnosisCodes = diagnosisCodes,
            ServiceLines = serviceLines,
            AdjustmentInfo = status switch
            {
                "Pended" => new()
                {
                    AdjustmentType = "Reversal Pending Review",
                    Reason = "Duplicate entry detected - awaiting confirmation",
                    AdjustmentAmount = -100.00m,
                    AdjustmentDate = DateTime.Now.AddDays(-1),
                    AdjustedBy = "claims-examiner-001"
                },
                _ => null
            },
            IsEditable = status == "Pended" || status == "InAdjudication",
            CanApprove = status == "Pended" || status == "InAdjudication",
            CanDeny = status == "Pended" || status == "InAdjudication",
            CanReverse = status == "Approved" || status == "Paid",
            AuditTrail = auditTrail,
            LineCount = serviceLines.Count
        };
    }

    public async Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AdjudicationTransparencyData>($"{baseUrl}/claims/{claimId}/adjudication-detail");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching adjudication data for claim {ClaimId}, returning mock data", claimId);
            return GetMockAdjudicationData(claimId);
        }
    }

    private AdjudicationTransparencyData GetMockAdjudicationData(string claimId)
    {
        var baseTime = DateTime.Now.AddDays(-2).AddHours(9);
        return new AdjudicationTransparencyData
        {
            Steps = new List<AdjudicationStep>
            {
                new() { StepNumber = 1, StepName = "Validate", Status = "Passed", Timestamp = baseTime, DurationMs = 12, Summary = "All required fields present, EDI structure valid" },
                new() { StepNumber = 2, StepName = "NCCI/MUE Edits", Status = "Passed", Timestamp = baseTime.AddMilliseconds(12), DurationMs = 48, Summary = "2 NCCI checks passed — no edit failures" },
                new() { StepNumber = 3, StepName = "Fee Schedule", Status = "Passed", Timestamp = baseTime.AddMilliseconds(60), DurationMs = 35, Summary = "Rates resolved: Medicare RVU @ 1.05× multiplier" },
                new() { StepNumber = 4, StepName = "Benefits", Status = "Passed", Timestamp = baseTime.AddMilliseconds(95), DurationMs = 28, Summary = "Benefit rules applied — copay $25 + 20% coinsurance" },
                new() { StepNumber = 5, StepName = "Auth Check", Status = "Passed", Timestamp = baseTime.AddMilliseconds(123), DurationMs = 8, Summary = "Auth AUTH-2024-78901 found, units within limit" },
                new() { StepNumber = 6, StepName = "Adjudicate", Status = "Passed", Timestamp = baseTime.AddMilliseconds(131), DurationMs = 22, Summary = "Claim approved — plan pays $192.00" },
                new() { StepNumber = 7, StepName = "Payment Staging", Status = "Passed", Timestamp = baseTime.AddMilliseconds(153), DurationMs = 9, Summary = "Queued for payment run PMTRUN-2026-0042" }
            },
            NcciResults = new List<NcciEditResult>
            {
                new() { EditCode = "CPT-MUE-99213", EditType = "MUE", Description = "Medically Unlikely Edit — 99213 office visit", Passed = true, AffectedProcedureCode = "99213", ResolutionApplied = "Units: 1 (limit 1) — within MUE limit" },
                new() { EditCode = "CPT-MUE-80053", EditType = "MUE", Description = "Medically Unlikely Edit — 80053 metabolic panel", Passed = true, AffectedProcedureCode = "80053", ResolutionApplied = "Units: 1 (limit 1) — within MUE limit" }
            },
            FeeScheduleResults = new List<FeeScheduleResult>
            {
                new() { ProcedureCode = "99213", Modifier = "", FeeScheduleName = "Medicare RVU 2026", BilledAmount = 150.00m, AllowedAmount = 125.00m, ContractedRate = 125.00m, RateBasis = "MedicareRVU", RateMultiplier = 1.05m, NetworkTier = "Tier1" },
                new() { ProcedureCode = "80053", Modifier = "", FeeScheduleName = "Medicare RVU 2026", BilledAmount = 85.00m, AllowedAmount = 75.00m, ContractedRate = 75.00m, RateBasis = "MedicareRVU", RateMultiplier = 1.05m, NetworkTier = "Tier1" },
                new() { ProcedureCode = "85025", Modifier = "", FeeScheduleName = "Medicare RVU 2026", BilledAmount = 45.00m, AllowedAmount = 40.00m, ContractedRate = 40.00m, RateBasis = "MedicareRVU", RateMultiplier = 1.05m, NetworkTier = "Tier1" }
            },
            BenefitCalculation = new BenefitCalculationResult
            {
                ServiceType = "Outpatient Office Visit",
                BenefitRuleApplied = "Medical-Office-Tier1-Rule",
                NetworkTier = "Tier1",
                AllowedAmount = 240.00m,
                DeductibleApplied = 0.00m,
                DeductibleRemaining = 750.00m,
                CopayAmount = 25.00m,
                CoinsuranceAmount = 43.00m,
                PlanPayment = 172.00m,
                MemberResponsibility = 68.00m,
                DeductibleMet = false,
                OopMaxMet = false,
                IndividualDeductibleBalance = 750.00m,
                IndividualDeductibleLimit = 1500.00m,
                IndividualOopBalance = 1250.00m,
                IndividualOopLimit = 5000.00m,
                AccumulatorUpdates = new List<AccumulatorUpdate>
                {
                    new() { AccumulatorType = "IndividualOop", AmountApplied = 68.00m, NewBalance = 1318.00m, Limit = 5000.00m }
                }
            }
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

    public async Task<MemberPcp?> GetMemberPcpAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberPcp>($"{baseUrl}/members/{memberId}/pcp");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching PCP for member {MemberId}, returning mock data", memberId);
            return GetMockPcp(memberId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error assigning PCP for member {MemberId}, simulating success", request.MemberId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching coverage history for member {MemberId}, returning mock data", memberId);
            return GetMockCoverageHistory(memberId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching 834 transactions for member {MemberId}, returning mock data", memberId);
            return GetMock834Transactions(memberId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error terminating enrollment for member {MemberId}, simulating success", request.MemberId);
        }
    }

    public async Task<MemberAccumulators> GetAccumulatorsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var accums = await _httpClient.GetFromJsonAsync<MemberAccumulators>($"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/accumulators");
            return accums ?? GetMockAccumulators(memberId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching accumulators for member {MemberId}, returning mock data", memberId);
            return GetMockAccumulators(memberId);
        }
    }

    private static MemberAccumulators GetMockAccumulators(string memberId)
    {
        return new MemberAccumulators
        {
            IndividualDeductibleUsed = 1247.00m,
            IndividualDeductibleLimit = 1500.00m,
            FamilyDeductibleUsed = 1893.00m,
            FamilyDeductibleLimit = 3000.00m,
            IndividualOopUsed = 1842.00m,
            IndividualOopLimit = 3000.00m,
            FamilyOopUsed = 2764.00m,
            FamilyOopLimit = 9000.00m,
            ServiceAccumulators = new List<ServiceAccumulator>
            {
                new() { ServiceType = "Physical Therapy", Used = 12, Limit = 20, UnitType = "visits" },
                new() { ServiceType = "Mental Health Outpatient", Used = 8, Limit = 30, UnitType = "visits" },
                new() { ServiceType = "Skilled Nursing", Used = 0, Limit = 60, UnitType = "days" }
            },
            RecentActivity = new List<AccumulatorActivity>
            {
                new() { ClaimId = "CLM-2026-00412", ServiceDate = DateTime.Today.AddDays(-3), DeductibleApplied = 0m, CopayApplied = 30.00m, CoinsuranceApplied = 47.60m, PlanPaid = 190.40m },
                new() { ClaimId = "CLM-2026-00398", ServiceDate = DateTime.Today.AddDays(-8), DeductibleApplied = 125.00m, CopayApplied = 0m, CoinsuranceApplied = 0m, PlanPaid = 275.00m },
                new() { ClaimId = "CLM-2026-00371", ServiceDate = DateTime.Today.AddDays(-15), DeductibleApplied = 0m, CopayApplied = 30.00m, CoinsuranceApplied = 34.00m, PlanPaid = 136.00m },
                new() { ClaimId = "CLM-2026-00355", ServiceDate = DateTime.Today.AddDays(-22), DeductibleApplied = 350.00m, CopayApplied = 0m, CoinsuranceApplied = 60.00m, PlanPaid = 240.00m },
                new() { ClaimId = "CLM-2026-00340", ServiceDate = DateTime.Today.AddDays(-30), DeductibleApplied = 200.00m, CopayApplied = 30.00m, CoinsuranceApplied = 0m, PlanPaid = 420.00m }
            }
        };
    }

    private MemberPcp GetMockPcp(string memberId)
    {
        return new MemberPcp
        {
            ProviderId = "PRV-001",
            ProviderName = "Dr. Priya Patel",
            NPI = "1234567890",
            Specialty = "Family Medicine",
            NetworkStatus = "In-Network",
            AssignedDate = DateTime.Now.AddYears(-1),
            PracticeName = "Seattle Medical Center",
            Phone = "(555) 123-4567"
        };
    }

    private List<CoverageHistoryEvent> GetMockCoverageHistory(string memberId)
    {
        return new List<CoverageHistoryEvent>
        {
            new()
            {
                EventId = $"EVT-{memberId}-001",
                EventDate = DateTime.Now.AddYears(-2),
                EventType = "Enrolled",
                Description = "Initial enrollment in Premium Health Plan",
                ChangedBy = "enrollment-system",
                NewValue = "Premium Health Plan (GRP-12345)"
            },
            new()
            {
                EventId = $"EVT-{memberId}-002",
                EventDate = DateTime.Now.AddMonths(-14),
                EventType = "PcpChange",
                Description = "PCP assignment changed",
                ChangedBy = "member-portal",
                OldValue = "Dr. Robert Kim (Family Medicine)",
                NewValue = "Dr. Priya Patel (Family Medicine)"
            },
            new()
            {
                EventId = $"EVT-{memberId}-003",
                EventDate = DateTime.Now.AddMonths(-6),
                EventType = "PlanChange",
                Description = "Annual open enrollment plan update",
                ChangedBy = "enrollment-system",
                OldValue = "Standard Health Plan",
                NewValue = "Premium Health Plan"
            },
            new()
            {
                EventId = $"EVT-{memberId}-004",
                EventDate = DateTime.Now.AddDays(-45),
                EventType = "Enrolled",
                Description = "Dental Plus coverage added",
                ChangedBy = "enrollment-system",
                NewValue = "Dental Plus (GRP-12345-D)"
            }
        };
    }

    private List<Enrollment834Record> GetMock834Transactions(string memberId)
    {
        return new List<Enrollment834Record>
        {
            new()
            {
                TransactionId = $"834-{memberId}-001",
                BatchId = "BATCH-2024-0891",
                MemberId = memberId,
                MemberName = "Sarah Johnson",
                MaintenanceTypeCode = "021",
                MaintenanceReasonCode = "27",
                TransactionSetPurpose = "Initial enrollment / Add subscriber",
                TransactionDate = DateTime.Now.AddYears(-2),
                Status = "Accepted",
                Errors = new List<string>(),
                RawSegmentPreview = "INS*Y*18*021*27*A*E**FT~REF*0F*MBR-2024-001~NM1*IL*1*JOHNSON*SARAH****34*123456789~"
            },
            new()
            {
                TransactionId = $"834-{memberId}-002",
                BatchId = "BATCH-2025-0234",
                MemberId = memberId,
                MemberName = "Sarah Johnson",
                MaintenanceTypeCode = "001",
                MaintenanceReasonCode = "01",
                TransactionSetPurpose = "Change - Plan change effective 01/01/2025",
                TransactionDate = DateTime.Now.AddMonths(-6),
                Status = "Accepted",
                Errors = new List<string>(),
                RawSegmentPreview = "INS*Y*18*001*01*A*E**FT~REF*0F*MBR-2024-001~HD*021**HLT*PREM2025*EMP~"
            },
            new()
            {
                TransactionId = $"834-{memberId}-003",
                BatchId = "BATCH-2025-0567",
                MemberId = memberId,
                MemberName = "Sarah Johnson",
                MaintenanceTypeCode = "001",
                MaintenanceReasonCode = "25",
                TransactionSetPurpose = "Change - Address update",
                TransactionDate = DateTime.Now.AddDays(-30),
                Status = "Rejected",
                Errors = new List<string>
                {
                    "834-E001: Member ID MBR-2024-001 not found in active enrollment roster",
                    "834-E014: State code 'WA' invalid for plan ID PREM2025 — plan restricted to OR only"
                },
                RawSegmentPreview = "INS*Y*18*001*25*A*E**FT~REF*0F*MBR-2024-001~N3*456 New St~N4*PORTLAND*OR*97201~"
            }
        };
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
    private static readonly List<AuthorizationSummary> _mockSubmittedAuthorizations = new();

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
            var mockAuths = GetMockAuthorizations(memberId);
            // Add user-submitted authorizations to the list
            mockAuths.InsertRange(0, _mockSubmittedAuthorizations.Where(a => 
                string.IsNullOrEmpty(memberId) || a.MemberName?.Contains(memberId, StringComparison.OrdinalIgnoreCase) == true));
            return mockAuths;
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
            _logger.LogWarning(ex, "Error submitting authorization, adding to mock data");
            var authId = $"AUTH-2026-{Random.Shared.Next(10000, 99999):D5}";
            
            // Add to mock submitted authorizations so it appears in the list
            _mockSubmittedAuthorizations.Insert(0, new AuthorizationSummary
            {
                AuthorizationId = authId,
                MemberName = $"Member {request.MemberId}",
                ProviderName = $"Provider {request.ProviderId}",
                ServiceType = request.ServiceType,
                Status = "Pending",
                RequestDate = DateTime.Now,
                DecisionDate = null,
                ProcessingTimeMs = 0
            });
            
            return authId;
        }
    }

    private async Task SetBearerTokenAsync()
    {
        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Authorization.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private List<AuthorizationSummary> GetMockAuthorizations(string? memberId)
    {
        var random = Random.Shared;
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

    public async Task<List<ServiceBenefitRule>> GetServiceBenefitRulesAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ServiceBenefitRule>>($"{baseUrl}/benefit-plans/{planId}/service-rules");
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching service benefit rules for plan {PlanId}, returning mock data", planId);
            return GetMockServiceBenefitRules(planId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating service benefit rules for plan {PlanId}, simulating success", request.PlanId);
        }
    }

    public async Task<AccumulatorConfiguration?> GetAccumulatorConfigAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AccumulatorConfiguration>($"{baseUrl}/benefit-plans/{planId}/accumulators");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching accumulator config for plan {PlanId}, returning mock data", planId);
            return GetMockAccumulatorConfig(planId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating accumulator config for plan {PlanId}, simulating success", planId);
        }
    }

    private List<ServiceBenefitRule> GetMockServiceBenefitRules(string planId)
    {
        return new List<ServiceBenefitRule>
        {
            new() { RuleId = $"{planId}-MED-T1-001", ServiceCategory = "Medical", ServiceTypeCode = "99201-99215", ServiceTypeDescription = "Office Visit", NetworkTier = "Tier1", Copay = 25.00m, CoinsurancePercent = 20m, SubjectToDeductible = false, PriorAuthRequired = false },
            new() { RuleId = $"{planId}-MED-T1-002", ServiceCategory = "Medical", ServiceTypeCode = "99221-99238", ServiceTypeDescription = "Inpatient Hospital", NetworkTier = "Tier1", Copay = 350.00m, CoinsurancePercent = 20m, SubjectToDeductible = true, PriorAuthRequired = true },
            new() { RuleId = $"{planId}-MED-T2-001", ServiceCategory = "Medical", ServiceTypeCode = "99201-99215", ServiceTypeDescription = "Office Visit", NetworkTier = "Tier2", Copay = 50.00m, CoinsurancePercent = 30m, SubjectToDeductible = false, PriorAuthRequired = false },
            new() { RuleId = $"{planId}-MED-OON-001", ServiceCategory = "Medical", ServiceTypeCode = "*", ServiceTypeDescription = "All Services (Out-of-Network)", NetworkTier = "OutOfNetwork", CoinsurancePercent = 50m, SubjectToDeductible = true, PriorAuthRequired = true },
            new() { RuleId = $"{planId}-PHR-T1-001", ServiceCategory = "Pharmacy", ServiceTypeCode = "GENERIC", ServiceTypeDescription = "Generic Drugs (Tier 1)", NetworkTier = "Tier1", Copay = 10.00m, SubjectToDeductible = false, PriorAuthRequired = false, CrossAccumulatesWithMedical = false },
            new() { RuleId = $"{planId}-PHR-T1-002", ServiceCategory = "Pharmacy", ServiceTypeCode = "PREFERRED-BRAND", ServiceTypeDescription = "Preferred Brand (Tier 2)", NetworkTier = "Tier1", Copay = 35.00m, SubjectToDeductible = false, PriorAuthRequired = false },
            new() { RuleId = $"{planId}-PHR-T1-003", ServiceCategory = "Pharmacy", ServiceTypeCode = "NON-PREFERRED-BRAND", ServiceTypeDescription = "Non-Preferred Brand (Tier 3)", NetworkTier = "Tier1", Copay = 65.00m, SubjectToDeductible = false, PriorAuthRequired = true },
            new() { RuleId = $"{planId}-MH-T1-001", ServiceCategory = "MentalHealth", ServiceTypeCode = "90791-90899", ServiceTypeDescription = "Mental Health / Behavioral Health", NetworkTier = "Tier1", Copay = 25.00m, CoinsurancePercent = 20m, SubjectToDeductible = false, AnnualVisitLimit = 52 },
            new() { RuleId = $"{planId}-DEN-T1-001", ServiceCategory = "Dental", ServiceTypeCode = "D0100-D9999", ServiceTypeDescription = "Dental (All Services)", NetworkTier = "Tier1", CoinsurancePercent = 20m, SubjectToDeductible = false, AnnualDollarLimit = 1500m, CrossAccumulatesWithMedical = false },
            new() { RuleId = $"{planId}-VIS-T1-001", ServiceCategory = "Vision", ServiceTypeCode = "92002-92014", ServiceTypeDescription = "Vision (Eye Exam)", NetworkTier = "Tier1", Copay = 15.00m, SubjectToDeductible = false, AnnualVisitLimit = 1, CrossAccumulatesWithMedical = false }
        };
    }

    private AccumulatorConfiguration GetMockAccumulatorConfig(string planId)
    {
        return new AccumulatorConfiguration
        {
            ConfigId = $"{planId}-ACC",
            PlanId = planId,
            IndividualDeductible = 1500.00m,
            FamilyDeductible = 3000.00m,
            IndividualOopMax = 5000.00m,
            FamilyOopMax = 10000.00m,
            PharmacyCrossAccumulatesDeductible = false,
            PharmacyCrossAccumulatesOop = true,
            DentalCrossAccumulatesOop = false,
            EmbeddedOrAggregate = "Embedded"
        };
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
        catch
        {
            return GetMockWorkflowRuns(limit);
        }
    }

    public async Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            return await _httpClient.GetFromJsonAsync<WorkflowDetails>($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}");
        }
        catch
        {
            return GetMockWorkflowDetails(workflowId);
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
        catch
        {
            return new List<WorkflowRun>();
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
        catch
        {
            return false;
        }
    }

    private List<WorkflowRun> GetMockWorkflowRuns(int limit)
    {
        var statuses = new[] { "Succeeded", "Succeeded", "Succeeded", "Failed", "Running" };
        return Enumerable.Range(1, Math.Min(limit, 10)).Select(i => new WorkflowRun
        {
            WorkflowId = $"claims-adjudication-{DateTime.Now.AddHours(-i * 2):yyyyMMddHHmm}-{i:D4}",
            Name = $"claims-adjudication-workflow-{i:D4}",
            Status = statuses[(i - 1) % statuses.Length],
            StartTime = DateTime.Now.AddHours(-i * 2),
            FinishTime = statuses[(i - 1) % statuses.Length] != "Running" ? DateTime.Now.AddHours(-i * 2).AddMinutes(8) : null,
            DurationSeconds = statuses[(i - 1) % statuses.Length] != "Running" ? 480 + i * 12 : 0
        }).ToList();
    }

    private WorkflowDetails GetMockWorkflowDetails(string workflowId)
    {
        return new WorkflowDetails
        {
            WorkflowId = workflowId,
            Name = workflowId,
            Status = "Succeeded",
            StartTime = DateTime.Now.AddHours(-2),
            FinishTime = DateTime.Now.AddHours(-2).AddMinutes(8),
            DurationSeconds = 480,
            Steps = new List<WorkflowStep>
            {
                new() { Name = "validate", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(12) },
                new() { Name = "ncci-edits", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(13), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(61) },
                new() { Name = "fee-schedule", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(62), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(97) },
                new() { Name = "benefits", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(98), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(126) },
                new() { Name = "auth-check", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(127), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(135) },
                new() { Name = "adjudicate", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(136), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(158) },
                new() { Name = "payment-staging", Status = "Succeeded", StartTime = DateTime.Now.AddHours(-2).AddSeconds(159), FinishTime = DateTime.Now.AddHours(-2).AddSeconds(167) }
            }
        };
    }
}

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

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        // TODO: Query Prometheus for real metrics
        // For now, return sample data
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

    public async Task<OperationalAlerts> GetOperationalAlertsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var alerts = await _httpClient.GetFromJsonAsync<OperationalAlerts>($"{baseUrl}/metrics/operational-alerts");
            return alerts ?? GetMockAlerts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching operational alerts, returning mock data");
            return GetMockAlerts();
        }
    }

    public async Task<EdiVolumeSummary> GetTodayEdiVolumeAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var volume = await _httpClient.GetFromJsonAsync<EdiVolumeSummary>($"{baseUrl}/metrics/edi-volume/today");
            return volume ?? GetMockEdiVolume();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching EDI volume, returning mock data");
            return GetMockEdiVolume();
        }
    }

    private static OperationalAlerts GetMockAlerts()
    {
        return new OperationalAlerts
        {
            WorkQueueCount = 40,
            PendingRfais = 5,
            AppealsDueThisWeek = 5,
            ApproachingFilingLimit = 3
        };
    }

    private static EdiVolumeSummary GetMockEdiVolume()
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
        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Attachments.ReadWrite" };
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

public class TenantService : ITenantService
{
    private readonly IMongoCollection<TenantSubscription> _tenantsCollection;
    private readonly IMongoCollection<BsonDocument> _membersCollection;
    private readonly ILogger<TenantService> _logger;

    public TenantService(IMongoClient mongoClient, IConfiguration configuration, ILogger<TenantService> logger)
    {
        _logger = logger;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _tenantsCollection = db.GetCollection<TenantSubscription>(
            configuration["MongoDB:TenantsCollection"] ?? "Tenants");
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
                SubscriptionStatus = "Trial",
                Tier = request.Tier,
                IsDemo = false,
                StripeCustomerId = request.StripeCustomerId,
                StripeSubscriptionId = request.StripeSubscriptionId,
                TrialEndsAt = now.AddDays(14),
                CreatedAt = now,
                UpdatedAt = now,
                AdminEmails = new List<string> { request.AdminEmail }
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

        // Validate submitter email before attempting to build MailAddress objects
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
            // Do not rethrow — email failure must not prevent a successful inquiry submission
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
                // Do not rethrow — confirmation email failure must not prevent a successful inquiry submission
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
        // Merge API results onto defaults so missing engines get "replace" mode
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

// ── PR14: EDI Operations Service ─────────────────────────────────────────────

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching 834 batches, returning mock data");
            return GetMock834Batches();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching 834 batch records for {BatchId}, returning mock data", batchId);
            return GetMock834Records(batchId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving 834 record, simulating success");
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching 277CA acknowledgments, returning mock data");
            return GetMock277CaAcknowledgments();
        }
    }

    public async Task<Stream> Download277CaAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/claims/{claimId}/277ca");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading 277CA for claim {ClaimId}, returning mock stream", claimId);
            var mockContent = $"ISA*00*          *00*          *ZZ*CLOUDHEALTH    *ZZ*PARTNER001     *260317*1200*^*00501*000000001*0*P*:~\nGS*FA*CLOUDHEALTH*PARTNER001*20260317*1200*1*X*005010X214~\nST*277*0001~\nBHT*0085*08*{claimId}*20260317*1200*TH~\nSE*4*0001~\nGE*1*1~\nIEA*1*000000001~";
            return new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(mockContent));
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching ERAs, returning mock data");
            return GetMockEras();
        }
    }

    public async Task<Stream> DownloadEraAsync(string paymentId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payments/{paymentId}/835");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading ERA for payment {PaymentId}, returning mock stream", paymentId);
            var mockContent = $"ISA*00*          *00*          *ZZ*CLOUDHEALTH    *ZZ*PARTNER001     *260317*1200*^*00501*000000001*0*P*:~\nGS*HP*CLOUDHEALTH*PARTNER001*20260317*1200*1*X*005010X221A1~\nST*835*0001~\nBPR*I*15420.00*C*ACH*CCP**01*021000021*DA*98765432*20260317**01*021000021*DA*12345678~\nTRN*1*CHK-{paymentId}*1234567890~\nSE*5*0001~\nGE*1*1~\nIEA*1*000000001~";
            return new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(mockContent));
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching EDI transaction history, returning mock data");
            return GetMockEdiHistory(transactionType, status, pageNumber, pageSize);
        }
    }

    private List<Edi834Batch> GetMock834Batches()
    {
        return new List<Edi834Batch>
        {
            new() { BatchId = "BATCH-2026-0101", TradingPartnerId = "TP-001", TradingPartnerName = "Availity", ReceivedDate = DateTime.Now.AddDays(-1), TotalRecords = 245, AcceptedCount = 243, RejectedCount = 2, PendingCount = 0, Status = "PartiallyAccepted", OriginalFileName = "834_20260316_001.txt" },
            new() { BatchId = "BATCH-2026-0098", TradingPartnerId = "TP-001", TradingPartnerName = "Availity", ReceivedDate = DateTime.Now.AddDays(-3), TotalRecords = 88, AcceptedCount = 88, RejectedCount = 0, PendingCount = 0, Status = "Completed", OriginalFileName = "834_20260314_001.txt" },
            new() { BatchId = "BATCH-2026-0091", TradingPartnerId = "TP-002", TradingPartnerName = "Change Healthcare", ReceivedDate = DateTime.Now.AddDays(-7), TotalRecords = 512, AcceptedCount = 498, RejectedCount = 9, PendingCount = 5, Status = "PartiallyAccepted", OriginalFileName = "ENROLL_20260310_CHC_001.edi" },
            new() { BatchId = "BATCH-2026-0085", TradingPartnerId = "TP-002", TradingPartnerName = "Change Healthcare", ReceivedDate = DateTime.Now.AddDays(-14), TotalRecords = 180, AcceptedCount = 180, RejectedCount = 0, PendingCount = 0, Status = "Completed", OriginalFileName = "ENROLL_20260303_CHC_001.edi" },
            new() { BatchId = "BATCH-2026-0079", TradingPartnerId = "TP-003", TradingPartnerName = "Waystar", ReceivedDate = DateTime.Now.AddDays(-21), TotalRecords = 45, AcceptedCount = 35, RejectedCount = 10, PendingCount = 0, Status = "PartiallyAccepted", OriginalFileName = "waystar_834_20260224.edi" }
        };
    }

    private List<Enrollment834Record> GetMock834Records(string batchId)
    {
        var records = new List<Enrollment834Record>();
        var names = new[] { ("Sarah", "Johnson"), ("Michael", "Chen"), ("Emily", "Rodriguez"), ("David", "Thompson"), ("Jennifer", "Williams") };
        var random = new Random(batchId.GetHashCode());
        for (int i = 1; i <= 8; i++)
        {
            var name = names[random.Next(names.Length)];
            var isRejected = i == 3 || i == 7;
            records.Add(new Enrollment834Record
            {
                TransactionId = $"{batchId}-REC-{i:D3}",
                BatchId = batchId,
                MemberId = $"MBR-2024-00{i}",
                MemberName = $"{name.Item1} {name.Item2}",
                MaintenanceTypeCode = i <= 3 ? "021" : (i <= 6 ? "001" : "024"),
                MaintenanceReasonCode = "27",
                TransactionSetPurpose = i <= 3 ? "Add subscriber" : (i <= 6 ? "Change" : "Cancel enrollment"),
                TransactionDate = DateTime.Now.AddDays(-random.Next(1, 5)),
                Status = isRejected ? "Rejected" : "Accepted",
                Errors = isRejected ? new List<string>
                {
                    "834-E001: Member ID not found in active enrollment roster",
                    "834-E019: Plan code PREM2026 not valid for sponsor SPNSR10002"
                } : new List<string>()
            });
        }
        return records;
    }

    private List<ClaimAcknowledgmentSummary> GetMock277CaAcknowledgments()
    {
        var claims = new[] { "CLM-2026-00001", "CLM-2026-00002", "CLM-2026-00003", "CLM-2026-00005", "CLM-2026-00008" };
        var statuses = new[] { ("Accepted", "A1", "A6", "Receipt and preliminary adjudication"), ("Accepted", "A1", "A6", "Claim received"), ("Rejected", "A3", "A7", "Claim returned to submitter"), ("Pended", "A6", "A0", "Acknowledgment pended"), ("Accepted", "A1", "A6", "Receipt confirmed") };
        return claims.Select((claimId, i) => new ClaimAcknowledgmentSummary
        {
            AckId = $"ACK-2026-{i + 1:D5}",
            ClaimId = claimId,
            ClaimNumber = $"CLM{(i + 1):D8}",
            MemberName = new[] { "Sarah Johnson", "Michael Chen", "Emily Rodriguez", "David Thompson", "Jennifer Williams" }[i],
            ProviderName = new[] { "Seattle Medical Center", "Downtown Urgent Care", "West Coast Radiology", "City General Hospital", "Advanced Diagnostics Lab" }[i],
            GeneratedDate = DateTime.Now.AddDays(-(i + 1) * 3),
            AckStatus = statuses[i].Item1,
            StatusCategoryCode = statuses[i].Item2,
            StatusCode = statuses[i].Item3,
            StatusDescription = statuses[i].Item4
        }).ToList();
    }

    private List<EraSummary> GetMockEras()
    {
        return new List<EraSummary>
        {
            new() { EraId = "ERA-2026-0042", PaymentId = "PMT-2026-0042", PayerName = "Cloud Health Office", PayeeNPI = "1234567890", PayeeName = "Seattle Medical Center", PaymentDate = DateTime.Now.AddDays(-1), PaymentMethod = "ACH", CheckNumber = "ACH-20260316", TotalPaymentAmount = 15420.00m, ClaimCount = 18, Status = "Transmitted" },
            new() { EraId = "ERA-2026-0038", PaymentId = "PMT-2026-0038", PayerName = "Cloud Health Office", PayeeNPI = "1234567891", PayeeName = "Downtown Urgent Care", PaymentDate = DateTime.Now.AddDays(-5), PaymentMethod = "ACH", CheckNumber = "ACH-20260312", TotalPaymentAmount = 8750.00m, ClaimCount = 12, Status = "Acknowledged" },
            new() { EraId = "ERA-2026-0034", PaymentId = "PMT-2026-0034", PayerName = "Cloud Health Office", PayeeNPI = "1234567892", PayeeName = "West Coast Radiology", PaymentDate = DateTime.Now.AddDays(-8), PaymentMethod = "CHK", CheckNumber = "CHK-12345678", TotalPaymentAmount = 22100.00m, ClaimCount = 25, Status = "Acknowledged" },
            new() { EraId = "ERA-2026-0029", PaymentId = "PMT-2026-0029", PayerName = "Cloud Health Office", PayeeNPI = "1234567893", PayeeName = "City General Hospital", PaymentDate = DateTime.Now.AddDays(-14), PaymentMethod = "ACH", CheckNumber = "ACH-20260303", TotalPaymentAmount = 47820.50m, ClaimCount = 41, Status = "Acknowledged" }
        };
    }

    private List<EdiTransactionHistoryItem> GetMockEdiHistory(string? type, string? status, int page, int pageSize)
    {
        var types = new[] { "834", "835", "277CA", "270", "271", "278" };
        var allItems = Enumerable.Range(1, 50).Select(i => new EdiTransactionHistoryItem
        {
            TransactionId = $"TXN-2026-{i:D5}",
            TransactionType = types[(i - 1) % types.Length],
            TransactionDate = DateTime.Now.AddDays(-i),
            TradingPartnerId = i % 3 == 0 ? "TP-002" : "TP-001",
            TradingPartnerName = i % 3 == 0 ? "Change Healthcare" : "Availity",
            Direction = types[(i - 1) % types.Length] == "834" ? "Inbound" : "Outbound",
            Status = i % 7 == 0 ? "Failed" : (i % 5 == 0 ? "Rejected" : "Completed"),
            ErrorSummary = (i % 7 == 0) ? "Connection timeout after 30s" : null,
            RecordCount = (i % 3 + 1) * 15
        }).ToList();

        var filtered = allItems.AsEnumerable();
        if (!string.IsNullOrEmpty(type) && type != "All") filtered = filtered.Where(x => x.TransactionType == type);
        if (!string.IsNullOrEmpty(status) && status != "All") filtered = filtered.Where(x => x.Status == status);
        return filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    }
}

// ── PR15: Payment Run Service ─────────────────────────────────────────────────

public class PaymentRunService : IPaymentRunService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentRunService> _logger;
    private static readonly List<PaymentRunSummary> _mockRuns = new();
    private static bool _mockInitialized = false;

    public PaymentRunService(HttpClient httpClient, IConfiguration configuration, ILogger<PaymentRunService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        if (!_mockInitialized) { InitMockRuns(); _mockInitialized = true; }
    }

    private void InitMockRuns()
    {
        _mockRuns.AddRange(new[]
        {
            new PaymentRunSummary { RunId = "PMTRUN-2026-0042", RunName = "March 2026 Bi-Weekly Run #2", Status = "Completed", CreatedDate = DateTime.Now.AddDays(-2), StartedDate = DateTime.Now.AddDays(-2).AddMinutes(5), CompletedDate = DateTime.Now.AddDays(-2).AddMinutes(35), CreatedBy = "admin@cloudhealthoffice.com", ClaimCount = 87, ProcessedCount = 87, TotalAmount = 142850.00m, EraFileUrl = "era/PMT-2026-0042/835" },
            new PaymentRunSummary { RunId = "PMTRUN-2026-0038", RunName = "March 2026 Bi-Weekly Run #1", Status = "Completed", CreatedDate = DateTime.Now.AddDays(-9), StartedDate = DateTime.Now.AddDays(-9).AddMinutes(3), CompletedDate = DateTime.Now.AddDays(-9).AddMinutes(28), CreatedBy = "admin@cloudhealthoffice.com", ClaimCount = 112, ProcessedCount = 112, TotalAmount = 198450.75m, EraFileUrl = "era/PMT-2026-0038/835" },
            new PaymentRunSummary { RunId = "PMTRUN-2026-0031", RunName = "February 2026 Final Run", Status = "Completed", CreatedDate = DateTime.Now.AddDays(-16), StartedDate = DateTime.Now.AddDays(-16).AddMinutes(2), CompletedDate = DateTime.Now.AddDays(-16).AddMinutes(42), CreatedBy = "claims@cloudhealthoffice.com", ClaimCount = 203, ProcessedCount = 203, TotalAmount = 387200.50m, EraFileUrl = "era/PMT-2026-0031/835" },
            new PaymentRunSummary { RunId = "PMTRUN-2026-0025", RunName = "February 2026 Mid-Month Run", Status = "Failed", CreatedDate = DateTime.Now.AddDays(-23), StartedDate = DateTime.Now.AddDays(-23).AddMinutes(1), CompletedDate = null, CreatedBy = "admin@cloudhealthoffice.com", ClaimCount = 95, ProcessedCount = 42, TotalAmount = 0m, ErrorMessage = "Payment gateway timeout after processing 42 claims — rerun required" }
        });
    }

    public async Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PaymentRunSummary>>($"{baseUrl}/payment-runs?limit={limit}");
            return result ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching payment runs, returning mock data");
            return _mockRuns.Take(limit).ToList();
        }
    }

    public async Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<PaymentRunDetails>($"{baseUrl}/payment-runs/{runId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching payment run {RunId}, returning mock data", runId);
            return GetMockPaymentRunDetails(runId);
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
            return result?.RunId ?? $"PMTRUN-2026-{DateTime.Now.Ticks % 9999:D4}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating payment run, returning mock ID");
            var newId = $"PMTRUN-2026-{(_mockRuns.Count + 50):D4}";
            _mockRuns.Insert(0, new PaymentRunSummary
            {
                RunId = newId,
                RunName = request.RunName,
                Status = "Pending",
                CreatedDate = DateTime.Now,
                CreatedBy = "current-user",
                ClaimCount = 0,
                ProcessedCount = 0,
                TotalAmount = 0m
            });
            return newId;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cancelling payment run {RunId}, simulating success", runId);
            var run = _mockRuns.FirstOrDefault(r => r.RunId == runId);
            if (run != null) run.Status = "Cancelled";
        }
    }

    public async Task<Stream> DownloadEraForRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payment-runs/{runId}/835");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading ERA for run {RunId}, returning mock stream", runId);
            var mockContent = $"ISA*00*          *00*          *ZZ*CLOUDHEALTH    *ZZ*PARTNER001     *260317*1200*^*00501*000000001*0*P*:~\nGS*HP*CLOUDHEALTH*PARTNER001*20260317*1200*1*X*005010X221A1~\nST*835*0001~\nBPR*I*142850.00*C*ACH*CCP**01*021000021*DA*98765432*20260316**01*021000021*DA*12345678~\nTRN*1*{runId}*1234567890~\nSE*5*0001~\nGE*1*1~\nIEA*1*000000001~";
            return new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(mockContent));
        }
    }

    private PaymentRunDetails GetMockPaymentRunDetails(string runId)
    {
        var summary = _mockRuns.FirstOrDefault(r => r.RunId == runId) ?? new PaymentRunSummary { RunId = runId, RunName = "Unknown Run", Status = "Unknown", CreatedDate = DateTime.Now, CreatedBy = "system" };
        var claims = Enumerable.Range(1, Math.Min(summary.ClaimCount > 0 ? summary.ClaimCount : 10, 25)).Select(i => new PaymentRunClaimItem
        {
            ClaimId = $"CLM-2026-{i:D5}",
            ClaimNumber = $"CLM{i:D8}",
            MemberName = new[] { "Sarah Johnson", "Michael Chen", "Emily Rodriguez", "David Thompson" }[i % 4],
            ProviderName = new[] { "Seattle Medical Center", "Downtown Urgent Care", "West Coast Radiology", "City General Hospital" }[i % 4],
            ChargeAmount = (i + 1) * 250.00m,
            AllowedAmount = (i + 1) * 210.00m,
            PaidAmount = (i + 1) * 168.00m,
            MemberResponsibility = (i + 1) * 42.00m,
            PaymentStatus = i % 9 == 0 ? "Excluded" : "Included"
        }).ToList();

        return new PaymentRunDetails
        {
            RunId = summary.RunId,
            RunName = summary.RunName,
            Status = summary.Status,
            CreatedDate = summary.CreatedDate,
            StartedDate = summary.StartedDate,
            CompletedDate = summary.CompletedDate,
            CreatedBy = summary.CreatedBy,
            ClaimCount = summary.ClaimCount > 0 ? summary.ClaimCount : claims.Count,
            ProcessedCount = summary.ProcessedCount > 0 ? summary.ProcessedCount : claims.Count,
            TotalAmount = summary.TotalAmount > 0 ? summary.TotalAmount : claims.Sum(c => c.PaidAmount),
            ErrorMessage = summary.ErrorMessage,
            EraFileUrl = summary.EraFileUrl,
            ClaimServiceDateFrom = DateTime.Now.AddDays(-30),
            ClaimServiceDateTo = DateTime.Now.AddDays(-1),
            TotalCharges = claims.Sum(c => c.ChargeAmount),
            TotalAllowed = claims.Sum(c => c.AllowedAmount),
            TotalMemberResponsibility = claims.Sum(c => c.MemberResponsibility),
            ApprovedCount = claims.Count(c => c.PaymentStatus == "Included"),
            DeniedCount = 0,
            AdjustmentCount = claims.Count(c => c.PaymentStatus == "Adjusted"),
            Claims = claims
        };
    }

    private class CreateRunResponse { public string RunId { get; set; } = string.Empty; }
}

// ── PR15: Premium Billing Service ────────────────────────────────────────────

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching billing cycles, returning mock data");
            return GetMockBillingCycles(sponsorId, status);
        }
    }

    public async Task<BillingCycleDetails?> GetBillingCycleByIdAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<BillingCycleDetails>($"{baseUrl}/billing-cycles/{cycleId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching billing cycle {CycleId}, returning mock data", cycleId);
            return GetMockBillingCycleDetails(cycleId);
        }
    }

    public async Task<string> GenerateInvoiceAsync(CreateInvoiceRequest request)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/billing-cycles", request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreateCycleResponse>())?.CycleId ?? Guid.NewGuid().ToString("N")[..8];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating invoice, simulating success");
            return $"CYC-{DateTime.Now:yyyyMM}-{new Random().Next(100, 999)}";
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching premium rates, returning mock data");
            return GetMockPremiumRates(planId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating premium rate {RateId}, simulating success", rateId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error marking cycle {CycleId} as paid, simulating success", cycleId);
        }
    }

    public async Task<Stream> DownloadInvoiceAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/billing-cycles/{cycleId}/invoice");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading invoice for cycle {CycleId}, returning mock stream", cycleId);
            var mockContent = $"INVOICE\nCloud Health Office Premium Billing\nCycle ID: {cycleId}\nGenerated: {DateTime.Now:MM/dd/yyyy}\n\nThis is a mock invoice for demonstration purposes.";
            return new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(mockContent));
        }
    }

    private List<BillingCycle> GetMockBillingCycles(string? sponsorId, string? status)
    {
        var cycles = new List<BillingCycle>
        {
            new() { CycleId = "CYC-202603-001", SponsorId = "SPNSR10001", SponsorName = "Acme Corporation", BillingPeriod = "2026-03", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(14), TotalPremium = 48750.00m, Status = "Sent", InvoiceNumber = "INV-2026-0342", MemberCount = 125 },
            new() { CycleId = "CYC-202603-002", SponsorId = "SPNSR10002", SponsorName = "Pacific Northwest Union", BillingPeriod = "2026-03", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(7), TotalPremium = 22180.50m, Status = "Sent", InvoiceNumber = "INV-2026-0343", MemberCount = 62 },
            new() { CycleId = "CYC-202603-003", SponsorId = "SPNSR10003", SponsorName = "TechStart Inc", BillingPeriod = "2026-03", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(21), TotalPremium = 11200.00m, Status = "Draft", MemberCount = 28 },
            new() { CycleId = "CYC-202602-001", SponsorId = "SPNSR10001", SponsorName = "Acme Corporation", BillingPeriod = "2026-02", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(-14), TotalPremium = 47500.00m, Status = "Paid", PaidDate = DateTime.Now.AddDays(-7), InvoiceNumber = "INV-2026-0298", MemberCount = 122 },
            new() { CycleId = "CYC-202602-002", SponsorId = "SPNSR10002", SponsorName = "Pacific Northwest Union", BillingPeriod = "2026-02", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(-21), TotalPremium = 21600.00m, Status = "Overdue", InvoiceNumber = "INV-2026-0299", MemberCount = 60 },
            new() { CycleId = "CYC-202601-002", SponsorId = "SPNSR10002", SponsorName = "Pacific Northwest Union", BillingPeriod = "2026-01", BillingFrequency = "Monthly", DueDate = DateTime.Now.AddDays(-51), TotalPremium = 20850.00m, Status = "Overdue", InvoiceNumber = "INV-2026-0251", MemberCount = 59 }
        };

        if (!string.IsNullOrEmpty(sponsorId)) cycles = cycles.Where(c => c.SponsorId == sponsorId).ToList();
        if (!string.IsNullOrEmpty(status)) cycles = cycles.Where(c => c.Status == status).ToList();
        return cycles;
    }

    private BillingCycleDetails GetMockBillingCycleDetails(string cycleId)
    {
        var cycle = GetMockBillingCycles(null, null).FirstOrDefault(c => c.CycleId == cycleId) ?? new BillingCycle { CycleId = cycleId, SponsorName = "Unknown", Status = "Draft" };
        return new BillingCycleDetails
        {
            CycleId = cycle.CycleId,
            SponsorId = cycle.SponsorId,
            SponsorName = cycle.SponsorName,
            BillingPeriod = cycle.BillingPeriod,
            BillingFrequency = cycle.BillingFrequency,
            DueDate = cycle.DueDate,
            TotalPremium = cycle.TotalPremium,
            Status = cycle.Status,
            PaidDate = cycle.PaidDate,
            InvoiceNumber = cycle.InvoiceNumber,
            MemberCount = cycle.MemberCount,
            TaxAmount = cycle.TotalPremium * 0.025m,
            AdjustmentAmount = 0m,
            LineItems = new List<BillingLineItem>
            {
                new() { PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Employee", MemberCount = 45, UnitRate = 485.00m, SubTotal = 45 * 485.00m },
                new() { PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Employee+Spouse", MemberCount = 28, UnitRate = 920.00m, SubTotal = 28 * 920.00m },
                new() { PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Family", MemberCount = 22, UnitRate = 1380.00m, SubTotal = 22 * 1380.00m },
                new() { PlanId = "PLAN-HMO-001", PlanName = "HMO Standard Plan", CoverageLevel = "Employee", MemberCount = 30, UnitRate = 380.00m, SubTotal = 30 * 380.00m }
            }
        };
    }

    private List<PremiumRate> GetMockPremiumRates(string? planId)
    {
        var rates = new List<PremiumRate>
        {
            new() { RateId = "RATE-001", PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Employee", Rate = 485.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-002", PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Employee+Spouse", Rate = 920.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-003", PlanId = "PLAN-PPO-001", PlanName = "PPO Gold Plan", CoverageLevel = "Family", Rate = 1380.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-004", PlanId = "PLAN-HMO-001", PlanName = "HMO Standard Plan", CoverageLevel = "Employee", Rate = 380.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-005", PlanId = "PLAN-HMO-001", PlanName = "HMO Standard Plan", CoverageLevel = "Employee+Spouse", Rate = 720.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-006", PlanId = "PLAN-HMO-001", PlanName = "HMO Standard Plan", CoverageLevel = "Family", Rate = 1080.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-007", PlanId = "PLAN-HDHP-001", PlanName = "HDHP Bronze Plan", CoverageLevel = "Employee", Rate = 285.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-008", PlanId = "PLAN-HDHP-001", PlanName = "HDHP Bronze Plan", CoverageLevel = "Employee+Spouse", Rate = 540.00m, EffectiveDate = new DateTime(2026, 1, 1) },
            new() { RateId = "RATE-009", PlanId = "PLAN-HDHP-001", PlanName = "HDHP Bronze Plan", CoverageLevel = "Family", Rate = 810.00m, EffectiveDate = new DateTime(2026, 1, 1) }
        };
        if (!string.IsNullOrEmpty(planId)) rates = rates.Where(r => r.PlanId == planId).ToList();
        return rates;
    }

    private class CreateCycleResponse { public string CycleId { get; set; } = string.Empty; }
}

// ── PR17: Reporting Service ───────────────────────────────────────────────────

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
            return await result.Content.ReadFromJsonAsync<ClaimsSummaryReport>() ?? GetMockClaimsSummary(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating claims summary report, returning mock data");
            return GetMockClaimsSummary(request);
        }
    }

    public async Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/payment-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<PaymentSummaryReport>() ?? GetMockPaymentSummary(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating payment summary report, returning mock data");
            return GetMockPaymentSummary(request);
        }
    }

    public async Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/eligibility-stats", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<EligibilityStatsReport>() ?? GetMockEligibilityStats(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating eligibility stats report, returning mock data");
            return GetMockEligibilityStats(request);
        }
    }

    public async Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/auth-approval", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<AuthApprovalReport>() ?? GetMockAuthApproval(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating auth approval report, returning mock data");
            return GetMockAuthApproval(request);
        }
    }

    public async Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/provider-performance", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<List<ClaimsByProvider>>() ?? GetMockProviderPerformance();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error generating provider performance report, returning mock data");
            return GetMockProviderPerformance();
        }
    }

    private ClaimsSummaryReport GetMockClaimsSummary(ReportRequest req)
    {
        var days = (int)(req.DateTo - req.DateFrom).TotalDays;
        var daily = Enumerable.Range(0, days > 0 ? days : 30).Select(i => new ClaimsByDateBucket
        {
            Date = req.DateFrom.AddDays(i),
            Count = new Random(i).Next(8, 45),
            TotalAmount = new Random(i).Next(8000, 60000)
        }).ToList();

        return new ClaimsSummaryReport
        {
            PeriodFrom = req.DateFrom,
            PeriodTo = req.DateTo,
            TotalClaims = daily.Sum(d => d.Count),
            TotalCharges = daily.Sum(d => d.TotalAmount),
            TotalAllowed = daily.Sum(d => d.TotalAmount) * 0.85m,
            TotalPaid = daily.Sum(d => d.TotalAmount) * 0.72m,
            ApprovedCount = (int)(daily.Sum(d => d.Count) * 0.78),
            DeniedCount = (int)(daily.Sum(d => d.Count) * 0.12),
            PendedCount = (int)(daily.Sum(d => d.Count) * 0.10),
            ApprovalRate = 78.4,
            AvgClaimAmount = 850m,
            DailyBreakdown = daily,
            TopProviders = new List<ClaimsByProvider>
            {
                new() { ProviderId = "PRV-001", ProviderName = "Seattle Medical Center", Specialty = "Multi-Specialty", ClaimCount = 287, TotalBilled = 425000m, TotalPaid = 306000m, DenialRate = 8.2, AvgProcessingDays = 2.1 },
                new() { ProviderId = "PRV-002", ProviderName = "Downtown Urgent Care", Specialty = "Urgent Care", ClaimCount = 198, TotalBilled = 148500m, TotalPaid = 106920m, DenialRate = 5.1, AvgProcessingDays = 1.4 },
                new() { ProviderId = "PRV-003", ProviderName = "West Coast Radiology", Specialty = "Radiology", ClaimCount = 156, TotalBilled = 312000m, TotalPaid = 218400m, DenialRate = 11.3, AvgProcessingDays = 2.8 },
                new() { ProviderId = "PRV-004", ProviderName = "City General Hospital", Specialty = "Hospital", ClaimCount = 89, TotalBilled = 978000m, TotalPaid = 684600m, DenialRate = 4.5, AvgProcessingDays = 4.2 }
            },
            TopDiagnoses = new List<ClaimsByDiagnosis>
            {
                new() { DiagnosisCode = "E11.9", Description = "Type 2 diabetes mellitus without complications", ClaimCount = 312, TotalAmount = 478000m },
                new() { DiagnosisCode = "I10", Description = "Essential (primary) hypertension", ClaimCount = 289, TotalAmount = 221000m },
                new() { DiagnosisCode = "J06.9", Description = "Acute upper respiratory infection, unspecified", ClaimCount = 187, TotalAmount = 89500m },
                new() { DiagnosisCode = "M54.5", Description = "Low back pain", ClaimCount = 164, TotalAmount = 195000m },
                new() { DiagnosisCode = "F41.1", Description = "Generalized anxiety disorder", ClaimCount = 143, TotalAmount = 124000m }
            }
        };
    }

    private PaymentSummaryReport GetMockPaymentSummary(ReportRequest req)
    {
        return new PaymentSummaryReport
        {
            PeriodFrom = req.DateFrom,
            PeriodTo = req.DateTo,
            EraCount = 12,
            TotalEraAmount = 1284750.25m,
            AvgEraAmount = 107062.52m,
            ByPeriod = new List<EraByPeriod>
            {
                new() { Period = "2025-10", EraCount = 2, TotalAmount = 198450m },
                new() { Period = "2025-11", EraCount = 2, TotalAmount = 215800m },
                new() { Period = "2025-12", EraCount = 2, TotalAmount = 232100m },
                new() { Period = "2026-01", EraCount = 2, TotalAmount = 198200m },
                new() { Period = "2026-02", EraCount = 2, TotalAmount = 247350m },
                new() { Period = "2026-03", EraCount = 2, TotalAmount = 192850m }
            }
        };
    }

    private EligibilityStatsReport GetMockEligibilityStats(ReportRequest req)
    {
        return new EligibilityStatsReport
        {
            PeriodFrom = req.DateFrom,
            PeriodTo = req.DateTo,
            TotalRequests = 4821,
            EligibleCount = 4389,
            IneligibleCount = 432,
            EligibilityRate = 91.0,
            AvgResponseTimeMs = 248
        };
    }

    private AuthApprovalReport GetMockAuthApproval(ReportRequest req)
    {
        return new AuthApprovalReport
        {
            PeriodFrom = req.DateFrom,
            PeriodTo = req.DateTo,
            TotalRequests = 892,
            ApprovedCount = 754,
            DeniedCount = 98,
            PendingCount = 40,
            ApprovalRate = 84.5,
            AvgDecisionDays = 1.8,
            ByServiceType = new List<AuthByServiceType>
            {
                new() { ServiceType = "Inpatient Hospitalization", Count = 187, ApprovedCount = 165, DeniedCount = 14, ApprovalRate = 88.2, AvgDecisionDays = 2.1 },
                new() { ServiceType = "Specialty Office Visit", Count = 312, ApprovedCount = 278, DeniedCount = 28, ApprovalRate = 89.1, AvgDecisionDays = 1.2 },
                new() { ServiceType = "Outpatient Surgery", Count = 145, ApprovedCount = 118, DeniedCount = 22, ApprovalRate = 81.4, AvgDecisionDays = 2.8 },
                new() { ServiceType = "DME/Home Health", Count = 98, ApprovedCount = 72, DeniedCount = 18, ApprovalRate = 73.5, AvgDecisionDays = 3.2 },
                new() { ServiceType = "Behavioral Health", Count = 150, ApprovedCount = 121, DeniedCount = 16, ApprovalRate = 80.7, AvgDecisionDays = 1.4 }
            }
        };
    }

    private List<ClaimsByProvider> GetMockProviderPerformance()
    {
        return new List<ClaimsByProvider>
        {
            new() { ProviderId = "PRV-001", ProviderName = "Seattle Medical Center", Specialty = "Multi-Specialty", ClaimCount = 287, TotalBilled = 425000m, TotalPaid = 306000m, DenialRate = 8.2, AvgProcessingDays = 2.1 },
            new() { ProviderId = "PRV-002", ProviderName = "Downtown Urgent Care", Specialty = "Urgent Care", ClaimCount = 198, TotalBilled = 148500m, TotalPaid = 106920m, DenialRate = 5.1, AvgProcessingDays = 1.4 },
            new() { ProviderId = "PRV-003", ProviderName = "West Coast Radiology", Specialty = "Radiology", ClaimCount = 156, TotalBilled = 312000m, TotalPaid = 218400m, DenialRate = 11.3, AvgProcessingDays = 2.8 },
            new() { ProviderId = "PRV-004", ProviderName = "City General Hospital", Specialty = "Hospital", ClaimCount = 89, TotalBilled = 978000m, TotalPaid = 684600m, DenialRate = 4.5, AvgProcessingDays = 4.2 },
            new() { ProviderId = "PRV-005", ProviderName = "Advanced Diagnostics Lab", Specialty = "Laboratory", ClaimCount = 421, TotalBilled = 189450m, TotalPaid = 136404m, DenialRate = 2.8, AvgProcessingDays = 0.9 }
        };
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
            return summary ?? GetMockSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching work queue summary, returning mock data");
            return GetMockSummary();
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
            return items ?? GetMockQueueItems(queueType, assignedTo, limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching work queue items, returning mock data");
            return GetMockQueueItems(queueType, assignedTo, limit);
        }
    }

    public async Task AssignClaimAsync(string claimId, string assignTo)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            await _httpClient.PostAsJsonAsync($"{baseUrl}/work-queue/{Uri.EscapeDataString(claimId)}/assign",
                new { AssignTo = assignTo });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error assigning claim {ClaimId}, operation simulated", claimId);
        }
    }

    public async Task OverrideAsync(string claimId, string overrideReason)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            await _httpClient.PostAsJsonAsync($"{baseUrl}/work-queue/{Uri.EscapeDataString(claimId)}/override",
                new { Reason = overrideReason });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error overriding claim {ClaimId}, operation simulated", claimId);
        }
    }

    private static WorkQueueSummary GetMockSummary()
    {
        return new WorkQueueSummary
        {
            NcciEditFailures = 12,
            MissingAuth = 8,
            ProviderNotContracted = 6,
            CobRequired = 5,
            MedicalReview = 9
        };
    }

    private static List<WorkQueueItem> GetMockQueueItems(string? queueType, string? assignedTo, int limit = 100)
    {

        var items = new List<WorkQueueItem>
        {
            // NCCI/MUE Edit Failures (12)
            new() { ClaimId = "CLM-2026-04201", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today.AddDays(-9), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 9, Priority = "High", AssignedTo = "Sarah Williams", TotalCharged = 4250.00m, ProcedureCodes = new() { "29881", "29877" } },
            new() { ClaimId = "CLM-2026-04215", MemberName = "Angela Washington", MemberId = "MBR-8202", ProviderName = "Hill Country Orthopedic Associates", ServiceDate = DateTime.Today.AddDays(-7), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 7, Priority = "Medium", AssignedTo = "Sarah Williams", TotalCharged = 3100.00m, ProcedureCodes = new() { "76856", "76857" } },
            new() { ClaimId = "CLM-2026-04228", MemberName = "Thanh Le", MemberId = "MBR-8206", ProviderName = "James Chen, DO", ServiceDate = DateTime.Today.AddDays(-5), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 5, Priority = "Medium", AssignedTo = "James Martinez", TotalCharged = 1875.00m, ProcedureCodes = new() { "99214", "99214" } },
            new() { ClaimId = "CLM-2026-04231", MemberName = "Robert Johnson", MemberId = "MBR-8207", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today.AddDays(-4), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 4, Priority = "Medium", AssignedTo = "Sarah Williams", TotalCharged = 2640.00m, ProcedureCodes = new() { "27447", "27447" } },
            new() { ClaimId = "CLM-2026-04245", MemberName = "David Kim", MemberId = "MBR-8209", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today.AddDays(-3), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 3, Priority = "Medium", AssignedTo = "Priya Kapoor", TotalCharged = 1450.00m, ProcedureCodes = new() { "97140", "97530" } },
            new() { ClaimId = "CLM-2026-04260", MemberName = "Sophia Martinez", MemberId = "MBR-8208", ProviderName = "Linda Nguyen, DPT", ServiceDate = DateTime.Today.AddDays(-2), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 2, Priority = "Low", AssignedTo = "James Martinez", TotalCharged = 680.00m, ProcedureCodes = new() { "97110", "97140" } },
            new() { ClaimId = "CLM-2026-04268", MemberName = "William Henderson", MemberId = "MBR-8205", ProviderName = "David Patel, MD", ServiceDate = DateTime.Today.AddDays(-2), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 2, Priority = "Low", AssignedTo = "David Chen", TotalCharged = 920.00m, ProcedureCodes = new() { "71046", "71047" } },
            new() { ClaimId = "CLM-2026-04273", MemberName = "Michael O'Brien", MemberId = "MBR-8203", ProviderName = "Karen Mitchell, MD", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 1, Priority = "Low", AssignedTo = "Sarah Williams", TotalCharged = 2100.00m, ProcedureCodes = new() { "99283", "99284" } },
            new() { ClaimId = "CLM-2026-04280", MemberName = "Priya Sharma", MemberId = "MBR-8204", ProviderName = "James Chen, DO", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 1, Priority = "Low", AssignedTo = "Priya Kapoor", TotalCharged = 540.00m, ProcedureCodes = new() { "99213", "99213" } },
            new() { ClaimId = "CLM-2026-04285", MemberName = "Margaret Thompson", MemberId = "MBR-8210", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today, QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 0, Priority = "Low", AssignedTo = "David Chen", TotalCharged = 375.00m, ProcedureCodes = new() { "80053", "80061" } },
            new() { ClaimId = "CLM-2026-04289", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today, QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 0, Priority = "Low", AssignedTo = "James Martinez", TotalCharged = 1320.00m, ProcedureCodes = new() { "20610", "20611" } },
            new() { ClaimId = "CLM-2026-04292", MemberName = "Angela Washington", MemberId = "MBR-8202", ProviderName = "Hill Country Orthopedic Associates", ServiceDate = DateTime.Today, QueueReason = "NCCI/MUE Edit Failure", QueueReasonCode = "NCCI", DaysInQueue = 0, Priority = "Low", AssignedTo = "Priya Kapoor", TotalCharged = 890.00m, ProcedureCodes = new() { "73721", "73721" } },

            // Missing Prior Authorization (8)
            new() { ClaimId = "CLM-2026-04190", MemberName = "William Henderson", MemberId = "MBR-8205", ProviderName = "Lone Star Radiology Group", ServiceDate = DateTime.Today.AddDays(-12), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 12, Priority = "High", AssignedTo = "Priya Kapoor", TotalCharged = 14500.00m, ProcedureCodes = new() { "73721" } },
            new() { ClaimId = "CLM-2026-04198", MemberName = "Robert Johnson", MemberId = "MBR-8207", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today.AddDays(-10), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 10, Priority = "High", AssignedTo = "Sarah Williams", TotalCharged = 8750.00m, ProcedureCodes = new() { "27447" } },
            new() { ClaimId = "CLM-2026-04220", MemberName = "Sophia Martinez", MemberId = "MBR-8208", ProviderName = "David Patel, MD", ServiceDate = DateTime.Today.AddDays(-6), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 6, Priority = "Medium", AssignedTo = "James Martinez", TotalCharged = 3200.00m, ProcedureCodes = new() { "70553" } },
            new() { ClaimId = "CLM-2026-04237", MemberName = "Thanh Le", MemberId = "MBR-8206", ProviderName = "Karen Mitchell, MD", ServiceDate = DateTime.Today.AddDays(-4), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 4, Priority = "Medium", AssignedTo = "Priya Kapoor", TotalCharged = 2100.00m, ProcedureCodes = new() { "99223" } },
            new() { ClaimId = "CLM-2026-04250", MemberName = "David Kim", MemberId = "MBR-8209", ProviderName = "Linda Nguyen, DPT", ServiceDate = DateTime.Today.AddDays(-3), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 3, Priority = "Medium", AssignedTo = "David Chen", TotalCharged = 960.00m, ProcedureCodes = new() { "97110" } },
            new() { ClaimId = "CLM-2026-04263", MemberName = "Angela Washington", MemberId = "MBR-8202", ProviderName = "James Chen, DO", ServiceDate = DateTime.Today.AddDays(-2), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 2, Priority = "Low", AssignedTo = "Sarah Williams", TotalCharged = 1850.00m, ProcedureCodes = new() { "99215" } },
            new() { ClaimId = "CLM-2026-04275", MemberName = "Michael O'Brien", MemberId = "MBR-8203", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 1, Priority = "Low", AssignedTo = "James Martinez", TotalCharged = 4300.00m, ProcedureCodes = new() { "29881" } },
            new() { ClaimId = "CLM-2026-04288", MemberName = "Priya Sharma", MemberId = "MBR-8204", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today, QueueReason = "Missing Prior Authorization", QueueReasonCode = "AUTH", DaysInQueue = 0, Priority = "Low", AssignedTo = "Priya Kapoor", TotalCharged = 5600.00m, ProcedureCodes = new() { "27446" } },

            // Provider Not Contracted (6)
            new() { ClaimId = "CLM-2026-04185", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", ProviderName = "Austin Spine & Pain Center", ServiceDate = DateTime.Today.AddDays(-14), QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 14, Priority = "High", AssignedTo = "David Chen", TotalCharged = 18200.00m, ProcedureCodes = new() { "62323" } },
            new() { ClaimId = "CLM-2026-04210", MemberName = "Margaret Thompson", MemberId = "MBR-8210", ProviderName = "Premier Cardiology Associates", ServiceDate = DateTime.Today.AddDays(-8), QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 8, Priority = "High", AssignedTo = "James Martinez", TotalCharged = 6400.00m, ProcedureCodes = new() { "93306" } },
            new() { ClaimId = "CLM-2026-04233", MemberName = "Thanh Le", MemberId = "MBR-8206", ProviderName = "South Austin Dermatology", ServiceDate = DateTime.Today.AddDays(-5), QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 5, Priority = "Medium", AssignedTo = "Sarah Williams", TotalCharged = 1250.00m, ProcedureCodes = new() { "11102" } },
            new() { ClaimId = "CLM-2026-04252", MemberName = "William Henderson", MemberId = "MBR-8205", ProviderName = "Heart of Texas ENT", ServiceDate = DateTime.Today.AddDays(-3), QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 3, Priority = "Medium", AssignedTo = "Priya Kapoor", TotalCharged = 2800.00m, ProcedureCodes = new() { "31231" } },
            new() { ClaimId = "CLM-2026-04270", MemberName = "Sophia Martinez", MemberId = "MBR-8208", ProviderName = "Westlake Allergy & Asthma", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 1, Priority = "Low", AssignedTo = "David Chen", TotalCharged = 475.00m, ProcedureCodes = new() { "95165" } },
            new() { ClaimId = "CLM-2026-04290", MemberName = "Robert Johnson", MemberId = "MBR-8207", ProviderName = "Capital Area Physical Therapy", ServiceDate = DateTime.Today, QueueReason = "Provider Not Contracted", QueueReasonCode = "OON", DaysInQueue = 0, Priority = "Low", AssignedTo = "James Martinez", TotalCharged = 720.00m, ProcedureCodes = new() { "97110" } },

            // COB/Other Payer Required (5)
            new() { ClaimId = "CLM-2026-04192", MemberName = "David Kim", MemberId = "MBR-8209", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today.AddDays(-11), QueueReason = "COB/Other Payer Required", QueueReasonCode = "COB", DaysInQueue = 11, Priority = "High", AssignedTo = "Priya Kapoor", TotalCharged = 7300.00m, ProcedureCodes = new() { "99215", "80053" } },
            new() { ClaimId = "CLM-2026-04218", MemberName = "Priya Sharma", MemberId = "MBR-8204", ProviderName = "James Chen, DO", ServiceDate = DateTime.Today.AddDays(-8), QueueReason = "COB/Other Payer Required", QueueReasonCode = "COB", DaysInQueue = 8, Priority = "High", AssignedTo = "David Chen", TotalCharged = 3450.00m, ProcedureCodes = new() { "99214", "85025" } },
            new() { ClaimId = "CLM-2026-04248", MemberName = "Angela Washington", MemberId = "MBR-8202", ProviderName = "Karen Mitchell, MD", ServiceDate = DateTime.Today.AddDays(-4), QueueReason = "COB/Other Payer Required", QueueReasonCode = "COB", DaysInQueue = 4, Priority = "Medium", AssignedTo = "Sarah Williams", TotalCharged = 2900.00m, ProcedureCodes = new() { "99283" } },
            new() { ClaimId = "CLM-2026-04265", MemberName = "Michael O'Brien", MemberId = "MBR-8203", ProviderName = "David Patel, MD", ServiceDate = DateTime.Today.AddDays(-2), QueueReason = "COB/Other Payer Required", QueueReasonCode = "COB", DaysInQueue = 2, Priority = "Low", AssignedTo = "James Martinez", TotalCharged = 1680.00m, ProcedureCodes = new() { "71046" } },
            new() { ClaimId = "CLM-2026-04283", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", ProviderName = "Linda Nguyen, DPT", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "COB/Other Payer Required", QueueReasonCode = "COB", DaysInQueue = 1, Priority = "Low", AssignedTo = "Priya Kapoor", TotalCharged = 440.00m, ProcedureCodes = new() { "97110" } },

            // Medical Review Required (9)
            new() { ClaimId = "CLM-2026-04188", MemberName = "William Henderson", MemberId = "MBR-8205", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today.AddDays(-13), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 13, Priority = "High", AssignedTo = "Sarah Williams", TotalCharged = 22400.00m, ProcedureCodes = new() { "27447" } },
            new() { ClaimId = "CLM-2026-04195", MemberName = "Robert Johnson", MemberId = "MBR-8207", ProviderName = "Hill Country Orthopedic Associates", ServiceDate = DateTime.Today.AddDays(-10), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 10, Priority = "High", AssignedTo = "James Martinez", TotalCharged = 15800.00m, ProcedureCodes = new() { "29828" } },
            new() { ClaimId = "CLM-2026-04207", MemberName = "Margaret Thompson", MemberId = "MBR-8210", ProviderName = "Karen Mitchell, MD", ServiceDate = DateTime.Today.AddDays(-8), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 8, Priority = "High", AssignedTo = "Priya Kapoor", TotalCharged = 11200.00m, ProcedureCodes = new() { "99223", "99232" } },
            new() { ClaimId = "CLM-2026-04225", MemberName = "Sophia Martinez", MemberId = "MBR-8208", ProviderName = "Maria Santos, MD", ServiceDate = DateTime.Today.AddDays(-6), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 6, Priority = "Medium", AssignedTo = "David Chen", TotalCharged = 4800.00m, ProcedureCodes = new() { "99215", "90837" } },
            new() { ClaimId = "CLM-2026-04240", MemberName = "Thanh Le", MemberId = "MBR-8206", ProviderName = "Lone Star Radiology Group", ServiceDate = DateTime.Today.AddDays(-4), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 4, Priority = "Medium", AssignedTo = "Sarah Williams", TotalCharged = 6700.00m, ProcedureCodes = new() { "74177" } },
            new() { ClaimId = "CLM-2026-04255", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", ProviderName = "James Chen, DO", ServiceDate = DateTime.Today.AddDays(-3), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 3, Priority = "Medium", AssignedTo = "James Martinez", TotalCharged = 2150.00m, ProcedureCodes = new() { "99214", "90834" } },
            new() { ClaimId = "CLM-2026-04267", MemberName = "Priya Sharma", MemberId = "MBR-8204", ProviderName = "David Patel, MD", ServiceDate = DateTime.Today.AddDays(-2), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 2, Priority = "Low", AssignedTo = "Priya Kapoor", TotalCharged = 3350.00m, ProcedureCodes = new() { "70553" } },
            new() { ClaimId = "CLM-2026-04278", MemberName = "Angela Washington", MemberId = "MBR-8202", ProviderName = "Linda Nguyen, DPT", ServiceDate = DateTime.Today.AddDays(-1), QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 1, Priority = "Low", AssignedTo = "David Chen", TotalCharged = 580.00m, ProcedureCodes = new() { "97110", "97530" } },
            new() { ClaimId = "CLM-2026-04291", MemberName = "Michael O'Brien", MemberId = "MBR-8203", ProviderName = "Rebecca Okafor, MD", ServiceDate = DateTime.Today, QueueReason = "Medical Review Required", QueueReasonCode = "MED", DaysInQueue = 0, Priority = "Low", AssignedTo = "Sarah Williams", TotalCharged = 1900.00m, ProcedureCodes = new() { "20610" } },
        };

        if (!string.IsNullOrEmpty(queueType))
            items = items.Where(i => i.QueueReasonCode == queueType).ToList();
        if (!string.IsNullOrEmpty(assignedTo) && assignedTo != "All")
            items = items.Where(i => i.AssignedTo == assignedTo).ToList();

        return items.OrderByDescending(i => i.DaysInQueue).Take(limit).ToList();
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
            return summary ?? GetMockSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching enrollment daily summary, returning mock data");
            return GetMockSummary();
        }
    }

    public async Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<EnrollmentFile>>($"{baseUrl}/enrollment-ops/files?days={days}");
            return files ?? GetMockFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching recent enrollment files, returning mock data");
            return GetMockFiles();
        }
    }

    public async Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var detail = await _httpClient.GetFromJsonAsync<EnrollmentFileDetail>($"{baseUrl}/enrollment-ops/files/{Uri.EscapeDataString(fileId)}");
            return detail ?? GetMockFileDetail(fileId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching enrollment file detail {FileId}, returning mock data", fileId);
            return GetMockFileDetail(fileId);
        }
    }

    private static EnrollmentDailySummary GetMockSummary()
    {
        return new EnrollmentDailySummary
        {
            FilesReceived = 4,
            TotalTransactions = 347,
            MembersAdded = 42,
            MembersTermed = 18,
            MembersChanged = 276,
            ErrorCount = 11
        };
    }

    private static List<EnrollmentFile> GetMockFiles()
    {
        var today = DateTime.Today;
        return new List<EnrollmentFile>
        {
            new() { FileId = "834-20260318-001", FileName = "MEA_834_20260318_0600.edi", ReceivedTime = today.AddHours(6).AddMinutes(2), SponsorName = "Metro Employees Association", GroupNumber = "GRP-001-2026", TransactionCount = 185, AddedCount = 24, TermedCount = 8, ChangedCount = 149, RejectedCount = 4, Status = "Completed" },
            new() { FileId = "834-20260318-002", FileName = "RHC_834_20260318_0615.edi", ReceivedTime = today.AddHours(6).AddMinutes(17), SponsorName = "Regional Health Cooperative", GroupNumber = "GRP-001-2026-SG", TransactionCount = 52, AddedCount = 6, TermedCount = 3, ChangedCount = 43, RejectedCount = 0, Status = "Completed" },
            new() { FileId = "834-20260318-003", FileName = "TXE_834_20260318_0700.edi", ReceivedTime = today.AddHours(7).AddMinutes(1), SponsorName = "Texas Educators Benefit Trust", GroupNumber = "GRP-042-2026", TransactionCount = 78, AddedCount = 9, TermedCount = 5, ChangedCount = 57, RejectedCount = 7, Status = "Partial" },
            new() { FileId = "834-20260318-004", FileName = "LSM_834_20260318_0730.edi", ReceivedTime = today.AddHours(7).AddMinutes(32), SponsorName = "Lone Star Manufacturing", GroupNumber = "GRP-087-2026", TransactionCount = 32, AddedCount = 3, TermedCount = 2, ChangedCount = 27, RejectedCount = 0, Status = "Processing" },
            new() { FileId = "834-20260317-001", FileName = "MEA_834_20260317_0600.edi", ReceivedTime = today.AddDays(-1).AddHours(6).AddMinutes(1), SponsorName = "Metro Employees Association", GroupNumber = "GRP-001-2026", TransactionCount = 191, AddedCount = 12, TermedCount = 6, ChangedCount = 170, RejectedCount = 3, Status = "Completed" },
            new() { FileId = "834-20260317-002", FileName = "RHC_834_20260317_0615.edi", ReceivedTime = today.AddDays(-1).AddHours(6).AddMinutes(14), SponsorName = "Regional Health Cooperative", GroupNumber = "GRP-001-2026-SG", TransactionCount = 48, AddedCount = 4, TermedCount = 1, ChangedCount = 43, RejectedCount = 0, Status = "Completed" },
            new() { FileId = "834-20260316-001", FileName = "MEA_834_20260316_0600.edi", ReceivedTime = today.AddDays(-2).AddHours(6).AddMinutes(3), SponsorName = "Metro Employees Association", GroupNumber = "GRP-001-2026", TransactionCount = 188, AddedCount = 15, TermedCount = 9, ChangedCount = 163, RejectedCount = 1, Status = "Completed" },
            new() { FileId = "834-20260315-001", FileName = "ACI_834_20260315_0800.edi", ReceivedTime = today.AddDays(-3).AddHours(8).AddMinutes(5), SponsorName = "Austin City Employees", GroupNumber = "GRP-055-2026", TransactionCount = 0, AddedCount = 0, TermedCount = 0, ChangedCount = 0, RejectedCount = 0, Status = "Failed" },
        };
    }

    private static EnrollmentFileDetail GetMockFileDetail(string fileId)
    {
        var files = GetMockFiles();
        var file = files.FirstOrDefault(f => f.FileId == fileId) ?? files[0];

        var rejections = new List<EnrollmentRejection>();

        if (fileId == "834-20260318-001" || fileId == files[0].FileId)
        {
            rejections = new List<EnrollmentRejection>
            {
                new() { MemberId = "MBR-8247", MemberName = "Garcia, Roberto", ErrorCode = "834-E003", ErrorDescription = "Invalid date of birth format — expected CCYYMMDD, received '03/14/1968'", RawSegmentReference = "DMG*D8*03/14/1968*M~" },
                new() { MemberId = "MBR-8312", MemberName = "Petrov, Natasha", ErrorCode = "834-E007", ErrorDescription = "Subscriber not found — member ID does not match active enrollment roster", RawSegmentReference = "REF*0F*MBR-8312~INS*Y*18*001*25~" },
                new() { MemberId = "MBR-8156", MemberName = "Williams, Andre", ErrorCode = "834-E012", ErrorDescription = "Duplicate enrollment — member already has active coverage under same plan", RawSegmentReference = "INS*Y*18*021*AI~HD*021**HLT*GOLD-PPO*EMP~" },
                new() { MemberId = "MBR-8089", MemberName = "Chen, Mei-Lin", ErrorCode = "834-E015", ErrorDescription = "Coverage date gap — termination 02/28/2026, new effective 03/15/2026 leaves 14-day gap", RawSegmentReference = "DTP*348*D8*20260315~DTP*349*D8*20260228~" },
            };
        }
        else if (fileId == "834-20260318-003")
        {
            rejections = new List<EnrollmentRejection>
            {
                new() { MemberId = "MBR-9401", MemberName = "Thompson, Dale", ErrorCode = "834-E003", ErrorDescription = "Invalid date of birth format — expected CCYYMMDD, received '1985-07-22'", RawSegmentReference = "DMG*D8*1985-07-22*M~" },
                new() { MemberId = "MBR-9422", MemberName = "Reyes, Isabella", ErrorCode = "834-E007", ErrorDescription = "Subscriber not found — member ID does not match active enrollment roster", RawSegmentReference = "REF*0F*MBR-9422~INS*Y*18*024*01~" },
                new() { MemberId = "MBR-9410", MemberName = "Okonkwo, Chidi", ErrorCode = "834-E012", ErrorDescription = "Duplicate enrollment — member already has active coverage under same plan", RawSegmentReference = "INS*Y*18*021*AI~HD*021**HLT*SLV-HMO*EMP~" },
                new() { MemberId = "MBR-9388", MemberName = "Park, Sung-Ho", ErrorCode = "834-E021", ErrorDescription = "Invalid gender code — expected 'M' or 'F', received 'X' (not yet supported)", RawSegmentReference = "DMG*D8*19910404*X~" },
                new() { MemberId = "MBR-9415", MemberName = "Davis, Tameka", ErrorCode = "834-E015", ErrorDescription = "Coverage date gap — termination 03/01/2026, new effective 03/16/2026 leaves 14-day gap", RawSegmentReference = "DTP*348*D8*20260316~DTP*349*D8*20260301~" },
                new() { MemberId = "MBR-9430", MemberName = "Nguyen, Bao", ErrorCode = "834-E007", ErrorDescription = "Subscriber not found — member ID does not match active enrollment roster", RawSegmentReference = "REF*0F*MBR-9430~INS*N*19*021*AI~" },
                new() { MemberId = "MBR-9405", MemberName = "Martin, Josefina", ErrorCode = "834-E030", ErrorDescription = "Invalid ZIP code — '7870' is not a valid 5-digit or 9-digit ZIP", RawSegmentReference = "N4*AUSTIN*TX*7870~" },
            };
        }
        else if (fileId == "834-20260317-001")
        {
            rejections = new List<EnrollmentRejection>
            {
                new() { MemberId = "MBR-8201", MemberName = "Ramirez, Carlos", ErrorCode = "834-E015", ErrorDescription = "Coverage date gap — termination 03/10/2026, new effective 03/18/2026 leaves 7-day gap", RawSegmentReference = "DTP*348*D8*20260318~DTP*349*D8*20260310~" },
                new() { MemberId = "MBR-8334", MemberName = "Foster, Denise", ErrorCode = "834-E003", ErrorDescription = "Invalid date of birth format — expected CCYYMMDD, received ''", RawSegmentReference = "DMG*D8**F~" },
                new() { MemberId = "MBR-8290", MemberName = "Patel, Ravi", ErrorCode = "834-E012", ErrorDescription = "Duplicate enrollment — member already has active coverage under same plan", RawSegmentReference = "INS*Y*18*021*AI~HD*021**HLT*GOLD-PPO*EMP~" },
            };
        }

        return new EnrollmentFileDetail
        {
            FileId = file.FileId,
            FileName = file.FileName,
            ReceivedTime = file.ReceivedTime,
            SponsorName = file.SponsorName,
            GroupNumber = file.GroupNumber,
            TransactionCount = file.TransactionCount,
            AddedCount = file.AddedCount,
            TermedCount = file.TermedCount,
            ChangedCount = file.ChangedCount,
            RejectedCount = file.RejectedCount,
            Status = file.Status,
            Rejections = rejections
        };
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
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<AppealsSummary>($"{baseUrl}/appeals/summary");
            return summary ?? GetMockSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching appeals summary, returning mock data");
            return GetMockSummary();
        }
    }

    public async Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrEmpty(appealId)) queryParts.Add($"appealId={Uri.EscapeDataString(appealId)}");
            if (!string.IsNullOrEmpty(memberId)) queryParts.Add($"memberId={Uri.EscapeDataString(memberId)}");
            if (!string.IsNullOrEmpty(originalClaimId)) queryParts.Add($"originalClaimId={Uri.EscapeDataString(originalClaimId)}");
            var query = string.Join("&", queryParts);
            var results = await _httpClient.GetFromJsonAsync<List<AppealSummary>>($"{baseUrl}/appeals/search?{query}");
            return results ?? GetMockAppeals(appealId, memberId, originalClaimId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching appeals, returning mock data");
            return GetMockAppeals(appealId, memberId, originalClaimId);
        }
    }

    public async Task<AppealDetails?> GetAppealByIdAsync(string appealId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AppealDetails>($"{baseUrl}/appeals/{Uri.EscapeDataString(appealId)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching appeal {AppealId}, returning mock data", appealId);
            return GetMockAppealDetail(appealId);
        }
    }

    private static AppealsSummary GetMockSummary()
    {
        return new AppealsSummary
        {
            OpenAppeals = 14,
            UrgentExpedited = 2,
            DueThisWeek = 5,
            OverturnedRate = 34.8
        };
    }

    private static List<AppealSummary> GetMockAppeals(string? appealId, string? memberId, string? originalClaimId)
    {
        var today = DateTime.Today;
        var all = new List<AppealSummary>
        {
            // Expedited — tight deadlines
            new() { AppealId = "APL-2026-0001", MemberName = "William Henderson", MemberId = "MBR-8205", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01847", OriginalDecision = "Denied", OriginalDenialReason = "Medical necessity not established — lumbar MRI without conservative therapy trial", Status = "Under Review", IsExpedited = true, FiledDate = today.AddDays(-1), DueDate = today.AddDays(2), DaysRemaining = 2, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0002", MemberName = "Robert Johnson", MemberId = "MBR-8207", AppealType = "Authorization", OriginalDecisionId = "AUTH-2026-00007", OriginalDecision = "Denied", OriginalDenialReason = "MRI not medically necessary — insufficient clinical justification during routine physical", Status = "Under Review", IsExpedited = true, FiledDate = today.AddDays(-2), DueDate = today.AddDays(1), DaysRemaining = 1, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "At Risk" },

            // Standard — various stages
            new() { AppealId = "APL-2026-0003", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01590", OriginalDecision = "Denied", OriginalDenialReason = "Out-of-network provider without prior authorization", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-8), DueDate = today.AddDays(22), DaysRemaining = 22, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0004", MemberName = "Angela Washington", MemberId = "MBR-8202", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01622", OriginalDecision = "Denied", OriginalDenialReason = "Exceeded visit limit — 20 of 20 PT visits already utilized for plan year", Status = "Received", IsExpedited = false, FiledDate = today.AddDays(-2), DueDate = today.AddDays(28), DaysRemaining = 28, AssignedReviewer = "", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0005", MemberName = "Priya Sharma", MemberId = "MBR-8204", AppealType = "Authorization", OriginalDecisionId = "AUTH-2026-00012", OriginalDecision = "Denied", OriginalDenialReason = "Experimental / investigational — procedure not covered under plan benefits", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-15), DueDate = today.AddDays(15), DaysRemaining = 15, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0006", MemberName = "Michael O'Brien", MemberId = "MBR-8203", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01701", OriginalDecision = "Denied", OriginalDenialReason = "Duplicate claim — service already adjudicated under CLM-2026-01698", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-20), DueDate = today.AddDays(10), DaysRemaining = 10, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0007", MemberName = "Sophia Martinez", MemberId = "MBR-8208", AppealType = "Coverage", OriginalDecisionId = "COV-2026-00034", OriginalDecision = "Denied", OriginalDenialReason = "Service not covered — cosmetic procedure exclusion applies", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-22), DueDate = today.AddDays(8), DaysRemaining = 8, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0008", MemberName = "Thanh Le", MemberId = "MBR-8206", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01455", OriginalDecision = "Denied", OriginalDenialReason = "Timely filing exceeded — claim received 97 days after date of service (90-day limit)", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-25), DueDate = today.AddDays(5), DaysRemaining = 5, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0009", MemberName = "David Kim", MemberId = "MBR-8209", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01510", OriginalDecision = "Partial Denial", OriginalDenialReason = "Reimbursement reduced — billed amount exceeds usual and customary rate for region", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-26), DueDate = today.AddDays(4), DaysRemaining = 4, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "At Risk" },
            new() { AppealId = "APL-2026-0010", MemberName = "Margaret Thompson", MemberId = "MBR-8210", AppealType = "Authorization", OriginalDecisionId = "AUTH-2026-00019", OriginalDecision = "Denied", OriginalDenialReason = "Prior authorization request submitted after service rendered", Status = "Received", IsExpedited = false, FiledDate = today.AddDays(-1), DueDate = today.AddDays(29), DaysRemaining = 29, AssignedReviewer = "", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0011", MemberName = "Carlos Ramirez", MemberId = "MBR-8201", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01388", OriginalDecision = "Denied", OriginalDenialReason = "Non-covered benefit — acupuncture services excluded under current plan", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-27), DueDate = today.AddDays(3), DaysRemaining = 3, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "At Risk" },

            // Decided
            new() { AppealId = "APL-2026-0012", MemberName = "Angela Washington", MemberId = "MBR-8202", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01290", OriginalDecision = "Denied", OriginalDenialReason = "Medical necessity not established — elective procedure", Status = "Decision Made", IsExpedited = false, FiledDate = today.AddDays(-28), DueDate = today.AddDays(2), DaysRemaining = 2, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "On Track" },
            new() { AppealId = "APL-2026-0013", MemberName = "Robert Johnson", MemberId = "MBR-8207", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01195", OriginalDecision = "Denied", OriginalDenialReason = "Out-of-network provider without prior authorization", Status = "Decision Made", IsExpedited = false, FiledDate = today.AddDays(-30), DueDate = today, DaysRemaining = 0, AssignedReviewer = "Dr. Mark Torres", ComplianceStatus = "On Track" },

            // Escalated
            new() { AppealId = "APL-2026-0014", MemberName = "Priya Sharma", MemberId = "MBR-8204", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01102", OriginalDecision = "Denied", OriginalDenialReason = "Medical necessity not established — specialist referral required", Status = "Escalated", IsExpedited = false, FiledDate = today.AddDays(-29), DueDate = today.AddDays(1), DaysRemaining = 1, AssignedReviewer = "Medical Director", ComplianceStatus = "At Risk" },

            // Withdrawn
            new() { AppealId = "APL-2026-0015", MemberName = "Thanh Le", MemberId = "MBR-8206", AppealType = "Authorization", OriginalDecisionId = "AUTH-2026-00005", OriginalDecision = "Denied", OriginalDenialReason = "Exceeded visit limit", Status = "Withdrawn", IsExpedited = false, FiledDate = today.AddDays(-10), DueDate = today.AddDays(20), DaysRemaining = 20, AssignedReviewer = "", ComplianceStatus = "N/A" },

            // Overdue
            new() { AppealId = "APL-2026-0016", MemberName = "Michael O'Brien", MemberId = "MBR-8203", AppealType = "Claim", OriginalDecisionId = "CLM-2026-01050", OriginalDecision = "Denied", OriginalDenialReason = "Coordination of benefits — primary payer has not adjudicated", Status = "Under Review", IsExpedited = false, FiledDate = today.AddDays(-32), DueDate = today.AddDays(-2), DaysRemaining = -2, AssignedReviewer = "Dr. Sarah Williams", ComplianceStatus = "Overdue" },
        };

        // Filter
        if (!string.IsNullOrEmpty(appealId))
            return all.Where(a => a.AppealId.Contains(appealId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(memberId))
            return all.Where(a => a.MemberId.Contains(memberId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(originalClaimId))
            return all.Where(a => a.OriginalDecisionId.Contains(originalClaimId, StringComparison.OrdinalIgnoreCase)).ToList();

        return all;
    }

    private static AppealDetails? GetMockAppealDetail(string appealId)
    {
        var appeals = GetMockAppeals(null, null, null);
        var match = appeals.FirstOrDefault(a => a.AppealId == appealId);
        if (match == null) return null;

        var detail = new AppealDetails
        {
            AppealId = match.AppealId,
            MemberName = match.MemberName,
            MemberId = match.MemberId,
            AppealType = match.AppealType,
            OriginalDecisionId = match.OriginalDecisionId,
            OriginalDecision = match.OriginalDecision,
            OriginalDenialReason = match.OriginalDenialReason,
            Status = match.Status,
            IsExpedited = match.IsExpedited,
            FiledDate = match.FiledDate,
            DueDate = match.DueDate,
            DaysRemaining = match.DaysRemaining,
            AssignedReviewer = match.AssignedReviewer,
            ComplianceStatus = match.ComplianceStatus,
            AppealReason = match.AppealId switch
            {
                "APL-2026-0001" => "Provider submitted additional clinical notes documenting 6-week conservative therapy trial including NSAIDs and physical therapy. Requests reconsideration based on updated medical records.",
                "APL-2026-0002" => "Orthopedic specialist states MRI is essential for differential diagnosis of suspected meniscal tear. Patient has persistent pain unresponsive to conservative management.",
                "APL-2026-0003" => "Member was seen at emergency department while traveling. Emergency exception to network requirements should apply per plan terms.",
                "APL-2026-0012" => "Surgeon has provided peer-reviewed literature supporting medical necessity of the procedure for the patient's specific condition and BMI category.",
                "APL-2026-0013" => "Member's employer confirmed urgent care visit was a covered emergency. Requests out-of-network emergency exception per state mandate.",
                _ => "Member/provider disagrees with denial determination and requests reconsideration with supporting documentation."
            },
            Documents = new List<AppealDocument>
            {
                new() { DocumentId = $"DOC-{match.AppealId}-001", DocumentName = "Appeal Letter.pdf", DocumentType = "Appeal Form", UploadedDate = match.FiledDate, UploadedBy = "Provider Portal" },
                new() { DocumentId = $"DOC-{match.AppealId}-002", DocumentName = "Clinical Notes.pdf", DocumentType = "Medical Records", UploadedDate = match.FiledDate.AddDays(1), UploadedBy = "Provider Portal" },
            },
            Timeline = new List<AppealTimelineEvent>
            {
                new() { EventDate = match.FiledDate, EventType = "Filed", Description = "Appeal received and logged", PerformedBy = "System" },
                new() { EventDate = match.FiledDate.AddHours(2), EventType = "Acknowledged", Description = "Acknowledgment letter sent to member", PerformedBy = "System" },
            }
        };

        if (!string.IsNullOrEmpty(match.AssignedReviewer) && match.AssignedReviewer != "Medical Director")
        {
            detail.Timeline.Add(new AppealTimelineEvent
            {
                EventDate = match.FiledDate.AddDays(1),
                EventType = "Assigned",
                Description = $"Assigned to {match.AssignedReviewer} for clinical review",
                PerformedBy = "Appeals Coordinator"
            });
        }

        if (match.Status == "Escalated")
        {
            detail.Timeline.Add(new AppealTimelineEvent
            {
                EventDate = DateTime.Today.AddDays(-2),
                EventType = "Escalated",
                Description = "Escalated to Medical Director — approaching regulatory deadline",
                PerformedBy = match.AssignedReviewer
            });
        }

        if (match.Status == "Decision Made")
        {
            var isOverturned = match.AppealId == "APL-2026-0013";
            detail.FinalDecision = isOverturned ? "Overturned" : "Upheld";
            detail.DecisionDate = match.DueDate.AddDays(-3);
            detail.FinalDecisionNotes = isOverturned
                ? "Original denial overturned. Emergency exception applies — out-of-network visit meets prudent layperson standard. Claim to be reprocessed at in-network benefit level."
                : "Original denial upheld. Clinical documentation does not support medical necessity for the requested elective procedure. Member retains right to external review.";
            detail.Timeline.Add(new AppealTimelineEvent
            {
                EventDate = detail.DecisionDate.Value,
                EventType = "Decision",
                Description = $"Decision: {detail.FinalDecision} — {(isOverturned ? "claim reprocessed" : "denial upheld")}",
                PerformedBy = match.AssignedReviewer
            });
            detail.Timeline.Add(new AppealTimelineEvent
            {
                EventDate = detail.DecisionDate.Value.AddHours(4),
                EventType = "Notified",
                Description = "Decision letter sent to member and provider",
                PerformedBy = "System"
            });
        }

        if (match.Status == "Withdrawn")
        {
            detail.FinalDecision = "Withdrawn";
            detail.DecisionDate = match.FiledDate.AddDays(5);
            detail.FinalDecisionNotes = "Member withdrew appeal — authorization was subsequently approved through standard process.";
            detail.Timeline.Add(new AppealTimelineEvent
            {
                EventDate = detail.DecisionDate.Value,
                EventType = "Withdrawn",
                Description = "Appeal withdrawn by member",
                PerformedBy = "Member Portal"
            });
        }

        return detail;
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
            return summary ?? GetMockSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching correspondence summary, returning mock data");
            return GetMockSummary();
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
            return items ?? GetMockQueue(type, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching correspondence queue, returning mock data");
            return GetMockQueue(type, status);
        }
    }

    public async Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var items = await _httpClient.GetFromJsonAsync<List<RfaiTrackingItem>>($"{baseUrl}/correspondence/rfais/outstanding");
            return items ?? GetMockRfais();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching outstanding RFAIs, returning mock data");
            return GetMockRfais();
        }
    }

    private static CorrespondenceSummary GetMockSummary()
    {
        return new CorrespondenceSummary
        {
            PendingGeneration = 14,
            GeneratedToday = 37,
            SentThisWeek = 218,
            FailedReturned = 6
        };
    }

    private static List<CorrespondenceItem> GetMockQueue(string? type, string? status)
    {
        var today = DateTime.Today;
        var items = new List<CorrespondenceItem>
        {
            // Adverse Determination letters
            new() { LetterId = "LTR-2026-05001", LetterType = "Adverse Determination", RecipientName = "William Henderson", RecipientType = "Member", RelatedId = "CLM-2026-01847", GeneratedDate = today, Status = "Generated", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05002", LetterType = "Adverse Determination", RecipientName = "Robert Johnson", RecipientType = "Member", RelatedId = "AUTH-2026-00007", GeneratedDate = today, Status = "Generated", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05003", LetterType = "Adverse Determination", RecipientName = "Thanh Le", RecipientType = "Member", RelatedId = "CLM-2026-01455", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05004", LetterType = "Adverse Determination", RecipientName = "Rebecca Okafor, MD", RecipientType = "Provider", RelatedId = "AUTH-2026-00007", GeneratedDate = today, Status = "Generated", DeliveryMethod = "Fax" },
            new() { LetterId = "LTR-2026-05005", LetterType = "Adverse Determination", RecipientName = "Sophia Martinez", RecipientType = "Member", RelatedId = "CLM-2026-01701", GeneratedDate = today.AddDays(-2), Status = "Sent", DeliveryMethod = "Portal" },

            // EOB letters
            new() { LetterId = "LTR-2026-05010", LetterType = "EOB", RecipientName = "Carlos Ramirez", RecipientType = "Member", RelatedId = "CLM-2026-01590", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Portal" },
            new() { LetterId = "LTR-2026-05011", LetterType = "EOB", RecipientName = "Angela Washington", RecipientType = "Member", RelatedId = "CLM-2026-01622", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Portal" },
            new() { LetterId = "LTR-2026-05012", LetterType = "EOB", RecipientName = "Priya Sharma", RecipientType = "Member", RelatedId = "CLM-2026-00398", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Email" },
            new() { LetterId = "LTR-2026-05013", LetterType = "EOB", RecipientName = "Michael O'Brien", RecipientType = "Member", RelatedId = "CLM-2026-00371", GeneratedDate = today.AddDays(-3), Status = "Delivered", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05014", LetterType = "EOB", RecipientName = "David Kim", RecipientType = "Member", RelatedId = "CLM-2026-00355", GeneratedDate = today.AddDays(-4), Status = "Delivered", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05015", LetterType = "EOB", RecipientName = "Margaret Thompson", RecipientType = "Member", RelatedId = "CLM-2026-00340", GeneratedDate = today.AddDays(-5), Status = "Returned", DeliveryMethod = "Mail" },

            // RFAI letters
            new() { LetterId = "LTR-2026-05020", LetterType = "RFAI", RecipientName = "Maria Santos, MD", RecipientType = "Provider", RelatedId = "CLM-2026-04201", GeneratedDate = today, Status = "Queued", DeliveryMethod = "Fax" },
            new() { LetterId = "LTR-2026-05021", LetterType = "RFAI", RecipientName = "Hill Country Orthopedic Associates", RecipientType = "Provider", RelatedId = "CLM-2026-04215", GeneratedDate = today, Status = "Queued", DeliveryMethod = "Fax" },
            new() { LetterId = "LTR-2026-05022", LetterType = "RFAI", RecipientName = "James Chen, DO", RecipientType = "Provider", RelatedId = "CLM-2026-04228", GeneratedDate = today.AddDays(-3), Status = "Sent", DeliveryMethod = "Fax" },
            new() { LetterId = "LTR-2026-05023", LetterType = "RFAI", RecipientName = "Rebecca Okafor, MD", RecipientType = "Provider", RelatedId = "CLM-2026-04231", GeneratedDate = today.AddDays(-12), Status = "Sent", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05024", LetterType = "RFAI", RecipientName = "Karen Mitchell, MD", RecipientType = "Provider", RelatedId = "CLM-2026-04237", GeneratedDate = today.AddDays(-28), Status = "Sent", DeliveryMethod = "Fax" },

            // Welcome Letters
            new() { LetterId = "LTR-2026-05030", LetterType = "Welcome Letter", RecipientName = "New Member Batch — MEA March 2026", RecipientType = "Member", RelatedId = "834-20260318-001", GeneratedDate = today, Status = "Queued", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05031", LetterType = "Welcome Letter", RecipientName = "New Member Batch — RHC March 2026", RecipientType = "Member", RelatedId = "834-20260318-002", GeneratedDate = today, Status = "Queued", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05032", LetterType = "Welcome Letter", RecipientName = "New Member Batch — TXE March 2026", RecipientType = "Member", RelatedId = "834-20260318-003", GeneratedDate = today, Status = "Queued", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05033", LetterType = "Welcome Letter", RecipientName = "Carlos Ramirez", RecipientType = "Member", RelatedId = "834-20260316-001", GeneratedDate = today.AddDays(-2), Status = "Sent", DeliveryMethod = "Mail" },

            // Payment Notices
            new() { LetterId = "LTR-2026-05040", LetterType = "Payment Notice", RecipientName = "Maria Santos, MD", RecipientType = "Provider", RelatedId = "PAY-2026-00112", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Email" },
            new() { LetterId = "LTR-2026-05041", LetterType = "Payment Notice", RecipientName = "James Chen, DO", RecipientType = "Provider", RelatedId = "PAY-2026-00113", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Email" },
            new() { LetterId = "LTR-2026-05042", LetterType = "Payment Notice", RecipientName = "Hill Country Orthopedic Associates", RecipientType = "Provider", RelatedId = "PAY-2026-00114", GeneratedDate = today.AddDays(-1), Status = "Sent", DeliveryMethod = "Portal" },
            new() { LetterId = "LTR-2026-05043", LetterType = "Payment Notice", RecipientName = "Rebecca Okafor, MD", RecipientType = "Provider", RelatedId = "PAY-2026-00115", GeneratedDate = today.AddDays(-3), Status = "Delivered", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05044", LetterType = "Payment Notice", RecipientName = "David Patel, MD", RecipientType = "Provider", RelatedId = "PAY-2026-00116", GeneratedDate = today.AddDays(-5), Status = "Failed", DeliveryMethod = "Fax" },

            // Additional queued items
            new() { LetterId = "LTR-2026-05050", LetterType = "Adverse Determination", RecipientName = "David Kim", RecipientType = "Member", RelatedId = "CLM-2026-01510", GeneratedDate = null, Status = "Queued", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05051", LetterType = "EOB", RecipientName = "Thanh Le", RecipientType = "Member", RelatedId = "CLM-2026-00412", GeneratedDate = null, Status = "Queued", DeliveryMethod = "Portal" },
            new() { LetterId = "LTR-2026-05052", LetterType = "Adverse Determination", RecipientName = "Margaret Thompson", RecipientType = "Member", RelatedId = "AUTH-2026-00019", GeneratedDate = null, Status = "Queued", DeliveryMethod = "Mail" },
            new() { LetterId = "LTR-2026-05053", LetterType = "Payment Notice", RecipientName = "Linda Nguyen, DPT", RecipientType = "Provider", RelatedId = "PAY-2026-00117", GeneratedDate = null, Status = "Queued", DeliveryMethod = "Email" },
            new() { LetterId = "LTR-2026-05054", LetterType = "EOB", RecipientName = "William Henderson", RecipientType = "Member", RelatedId = "CLM-2026-00340", GeneratedDate = today.AddDays(-6), Status = "Returned", DeliveryMethod = "Mail" },
        };

        if (!string.IsNullOrEmpty(type))
            items = items.Where(i => i.LetterType == type).ToList();
        if (!string.IsNullOrEmpty(status))
            items = items.Where(i => i.Status == status).ToList();

        return items;
    }

    private static List<RfaiTrackingItem> GetMockRfais()
    {
        var today = DateTime.Today;
        return new List<RfaiTrackingItem>
        {
            new() { RfaiId = "RFAI-2026-0301", RecipientName = "Maria Santos, MD", RecipientType = "Provider", RelatedClaimId = "CLM-2026-04201", DocumentsRequested = "Operative report, anesthesia record", SentDate = today.AddDays(-3), ResponseDeadline = today.AddDays(42), DaysSinceSent = 3, DaysUntilDeadline = 42, Status = "Awaiting Response" },
            new() { RfaiId = "RFAI-2026-0289", RecipientName = "James Chen, DO", RecipientType = "Provider", RelatedClaimId = "CLM-2026-04228", DocumentsRequested = "Office visit notes, referral documentation", SentDate = today.AddDays(-12), ResponseDeadline = today.AddDays(33), DaysSinceSent = 12, DaysUntilDeadline = 33, Status = "Awaiting Response" },
            new() { RfaiId = "RFAI-2026-0267", RecipientName = "Rebecca Okafor, MD", RecipientType = "Provider", RelatedClaimId = "CLM-2026-04231", DocumentsRequested = "Pre-surgical evaluation, imaging results, conservative treatment history", SentDate = today.AddDays(-28), ResponseDeadline = today.AddDays(17), DaysSinceSent = 28, DaysUntilDeadline = 17, Status = "Awaiting Response" },
            new() { RfaiId = "RFAI-2026-0248", RecipientName = "Karen Mitchell, MD", RecipientType = "Provider", RelatedClaimId = "CLM-2026-04237", DocumentsRequested = "Admission records, discharge summary", SentDate = today.AddDays(-35), ResponseDeadline = today.AddDays(10), DaysSinceSent = 35, DaysUntilDeadline = 10, Status = "Awaiting Response" },
            new() { RfaiId = "RFAI-2026-0231", RecipientName = "Lone Star Radiology Group", RecipientType = "Provider", RelatedClaimId = "CLM-2026-04190", DocumentsRequested = "Radiology report, order from referring physician, prior authorization number", SentDate = today.AddDays(-41), ResponseDeadline = today.AddDays(4), DaysSinceSent = 41, DaysUntilDeadline = 4, Status = "Approaching Deadline" },
        };
    }
}
