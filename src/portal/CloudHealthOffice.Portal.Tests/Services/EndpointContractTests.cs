using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Endpoint contract tests — verify that every URL the portal services call
/// is well-formed and consistent with the configured base URLs.
///
/// These tests use a recording HTTP handler that captures the actual request
/// URL, then asserts it matches the expected pattern. This catches:
///   - Base URL / controller route mismatches (e.g., /api vs /api/v1)
///   - Missing endpoint paths (e.g., /search, /recent)
///   - Typos in URL construction
///
/// These do NOT verify the backend has the endpoint (that requires integration
/// tests with WebApplicationFactory), but they catch the portal-side bugs that
/// caused most of the "Unable to connect" issues.
/// </summary>
public class EndpointContractTests
{
    /// <summary>
    /// Records all URLs that the HttpClient sends to, then returns a canned response.
    /// </summary>
    private class RecordingHandler : HttpMessageHandler
    {
        /// <summary>Full absolute URI including host, path, and query string</summary>
        public List<string> RecordedFullUrls { get; } = new();
        /// <summary>Path + query only (for assertions that don't care about host)</summary>
        public List<string> RecordedPaths { get; } = new();

        private readonly HttpStatusCode _responseCode;
        private readonly string _responseBody;

        public RecordingHandler(HttpStatusCode code = HttpStatusCode.OK, string body = "[]")
        {
            _responseCode = code;
            _responseBody = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordedFullUrls.Add(request.RequestUri?.AbsoluteUri ?? "");
            RecordedPaths.Add(request.RequestUri?.PathAndQuery ?? "");
            return Task.FromResult(new HttpResponseMessage(_responseCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> overrides)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://claims-service/api",
            ["Services:MemberService"] = "http://member-service/api/v1",
            ["Services:ProviderService"] = "http://provider-service/api",
            ["Services:CoverageService"] = "http://coverage-service/api",
            ["Services:AppealsService"] = "http://appeals-service/api",
            ["Services:CapitationService"] = "http://capitation-service/api",
            ["Services:PaymentService"] = "http://payment-service/api",
            ["Services:BillingService"] = "http://billing-service/api",
        };
        foreach (var kv in overrides) defaults[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    // ── Claims Service Contracts ──────────────────────────────────

    [Fact]
    public async Task ClaimsService_GetRecentClaims_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new ClaimsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<ClaimsService>>());

        await sut.GetRecentClaimsAsync(5);

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/claims/recent?count=5");
    }

    [Fact]
    public async Task ClaimsService_SearchClaims_CallsCorrectUrl()
    {
        var handler = new RecordingHandler(body: """{"claims":[],"totalCount":0}""");
        var config = BuildConfig(new());
        var sut = new ClaimsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<ClaimsService>>());

        await sut.SearchClaimsAsync(new ClaimSearchRequest { MemberId = "MEM001" });

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/claims/search");
    }

    [Fact]
    public async Task ClaimsService_GetClaimById_CallsCorrectUrl()
    {
        var handler = new RecordingHandler(body: "null");
        var config = BuildConfig(new());
        var sut = new ClaimsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<ClaimsService>>());

        await sut.GetClaimByIdAsync("CLM-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/claims/CLM-001");
    }

    // ── Member Service Contracts ──────────────────────────────────

    [Fact]
    public async Task MemberService_SearchMembers_CallsV1Url()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new MemberService(
            new HttpClient(handler) { BaseAddress = new Uri("http://member-service") },
            config, Mock.Of<ILogger<MemberService>>());

        // Use URL-unsafe characters to verify encoding
        await sut.SearchMembersAsync("O'Brien & Sons");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/v1/members/search?q=O")
            .And.Subject.Should().NotContain("&Sons", "ampersand should be URL-encoded");
    }

    [Fact]
    public async Task MemberService_GetMemberById_CallsV1Url()
    {
        var handler = new RecordingHandler(body: "null");
        var config = BuildConfig(new());
        var sut = new MemberService(
            new HttpClient(handler) { BaseAddress = new Uri("http://member-service") },
            config, Mock.Of<ILogger<MemberService>>());

        await sut.GetMemberByIdAsync("MEM-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/v1/members/MEM-001");
    }

    [Fact]
    public async Task MemberService_GetAccumulators_CallsV1Url()
    {
        var handler = new RecordingHandler(body: """{"individualDeductibleUsed":0}""");
        var config = BuildConfig(new());
        var sut = new MemberService(
            new HttpClient(handler) { BaseAddress = new Uri("http://member-service") },
            config, Mock.Of<ILogger<MemberService>>());

        await sut.GetAccumulatorsAsync("MEM-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/v1/members/MEM-001/accumulators");
    }

    [Fact]
    public async Task MemberService_GetPcp_CallsV1Url()
    {
        var handler = new RecordingHandler(body: "null");
        var config = BuildConfig(new());
        var sut = new MemberService(
            new HttpClient(handler) { BaseAddress = new Uri("http://member-service") },
            config, Mock.Of<ILogger<MemberService>>());

        await sut.GetMemberPcpAsync("MEM-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/v1/members/MEM-001/pcp");
    }

    // ── Appeals Service Contracts ─────────────────────────────────

    [Fact]
    public async Task AppealsService_GetSummary_CallsAppealsServiceUrl()
    {
        var handler = new RecordingHandler(body: """{"ncciEditFailures":0}""");
        var config = BuildConfig(new());
        var sut = new AppealsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://appeals-service") },
            config, Mock.Of<ILogger<AppealsService>>());

        await sut.GetSummaryAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/appeals/summary");
    }

    [Fact]
    public async Task AppealsService_Search_CallsAppealsServiceUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new AppealsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://appeals-service") },
            config, Mock.Of<ILogger<AppealsService>>());

        await sut.SearchAppealsAsync(memberId: "MEM-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Contain("/api/appeals/search")
            .And.Contain("memberId=MEM-001");
    }

    [Fact]
    public async Task AppealsService_DoesNotCallClaimsService()
    {
        var handler = new RecordingHandler(body: """{"ncciEditFailures":0}""");
        var config = BuildConfig(new());
        var sut = new AppealsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://appeals-service") },
            config, Mock.Of<ILogger<AppealsService>>());

        await sut.GetSummaryAsync();

        // Verify the full URL uses appeals-service host, NOT claims-service
        handler.RecordedFullUrls.Should().ContainSingle()
            .Which.Should().StartWith("http://appeals-service/");
    }

    // ── Authorization Service Contracts ────────────────────────────

    [Fact]
    public async Task AuthorizationService_Search_CallsSearchEndpoint()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new()
        {
            ["Services:AuthorizationService"] = "http://authorization-service/api"
        });
        var sut = new AuthorizationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://authorization-service") },
            config, Mock.Of<ILogger<AuthorizationService>>(),
            Mock.Of<Microsoft.Identity.Web.ITokenAcquisition>());

        await sut.GetAuthorizationsAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/authorizations/search");
    }

    [Fact]
    public async Task AuthorizationService_SearchWithMember_IncludesMemberIdParam()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new()
        {
            ["Services:AuthorizationService"] = "http://authorization-service/api"
        });
        var sut = new AuthorizationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://authorization-service") },
            config, Mock.Of<ILogger<AuthorizationService>>(),
            Mock.Of<Microsoft.Identity.Web.ITokenAcquisition>());

        await sut.GetAuthorizationsAsync(memberId: "MBR-007");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Contain("/api/authorizations/search?memberId=MBR-007");
    }

    [Fact]
    public async Task AuthorizationService_GetById_CallsCorrectUrl()
    {
        var handler = new RecordingHandler(body: "null");
        var config = BuildConfig(new()
        {
            ["Services:AuthorizationService"] = "http://authorization-service/api"
        });
        var sut = new AuthorizationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://authorization-service") },
            config, Mock.Of<ILogger<AuthorizationService>>(),
            Mock.Of<Microsoft.Identity.Web.ITokenAcquisition>());

        await sut.GetAuthorizationByIdAsync("AUTH-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/authorizations/AUTH-001");
    }

    // ── Coverage Service Contracts ───────────────────────────────

    [Fact]
    public async Task CoverageService_GetByMemberId_CallsHistoryEndpoint()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new CoverageService(
            new HttpClient(handler) { BaseAddress = new Uri("http://coverage-service") },
            config, Mock.Of<ILogger<CoverageService>>());

        await sut.GetCoverageByMemberIdAsync("MBR-001");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/v1/coverage/member/MBR-001/history");
    }

    // ── Metrics Service Contracts ────────────────────────────────

    [Fact]
    public async Task MetricsService_GetDashboardMetrics_CallsSummaryEndpoint()
    {
        var handler = new RecordingHandler(body: """{"totalClaims":0,"approvedClaims":0,"deniedClaims":0,"pendedClaims":0,"paidClaims":0,"totalChargeAmount":0,"totalAllowedAmount":0,"totalPaidAmount":0,"averageProcessingDays":0,"approvalRate":0}""");
        var config = BuildConfig(new());
        var sut = new MetricsService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<MetricsService>>());

        await sut.GetDashboardMetricsAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/claims/summary");
    }

    // ── Eligibility Service Contracts ────────────────────────────

    [Fact]
    public async Task EligibilityService_SubmitInquiry_CallsInquiryEndpoint()
    {
        var handler = new RecordingHandler(body: """{"id":"test","isEligible":true}""");
        var config = BuildConfig(new()
        {
            ["Services:EligibilityService"] = "http://eligibility-service/api"
        });
        var sut = new Portal.Services.EligibilityService(
            new HttpClient(handler) { BaseAddress = new Uri("http://eligibility-service") },
            config, Mock.Of<ILogger<Portal.Services.EligibilityService>>());

        await sut.CheckEligibilityAsync(new { subscriberId = "SUB-001" });

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/eligibility/inquiry");
    }

    // ── Payment Service Contracts ────────────────────────────────

    [Fact]
    public async Task PaymentRunService_GetRuns_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new PaymentRunService(
            new HttpClient(handler) { BaseAddress = new Uri("http://payment-service") },
            config, Mock.Of<ILogger<PaymentRunService>>());

        await sut.GetPaymentRunsAsync(10);

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/paymentruns")
            .And.Subject.Should().Contain("limit=10");
    }

    // ── Sponsor Service Contracts ────────────────────────────────

    [Fact]
    public async Task SponsorService_Search_CallsCorrectUrl()
    {
        var handler = new RecordingHandler(body: """{"sponsors":[]}""");
        var config = BuildConfig(new()
        {
            ["Services:SponsorService"] = "http://sponsor-service/api/v1"
        });
        var sut = new SponsorService(
            new HttpClient(handler) { BaseAddress = new Uri("http://sponsor-service") },
            config, Mock.Of<ILogger<SponsorService>>());

        await sut.SearchSponsorsAsync("Acme");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Contain("/api/v1/sponsors")
            .And.Contain("search=Acme");
    }

    // ── Benefit Plan Service Contracts ───────────────────────────

    [Fact]
    public async Task BenefitPlanService_GetAll_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new()
        {
            ["Services:BenefitPlanService"] = "http://benefit-plan-service/api"
        });
        var sut = new BenefitPlanService(
            new HttpClient(handler) { BaseAddress = new Uri("http://benefit-plan-service") },
            config, Mock.Of<ILogger<BenefitPlanService>>());

        await sut.GetBenefitPlansAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/v1/plans");
    }

    // ── Provider Service Contracts ────────────────────────────────

    [Fact]
    public async Task ProviderService_Search_SupportsQParam()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new ProviderService(
            new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") },
            config, Mock.Of<ILogger<ProviderService>>());

        // Use URL-unsafe characters to verify encoding
        await sut.SearchProvidersAsync("Chen & Associates");

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/providers/search?q=")
            .And.Subject.Should().NotContain("& A", "ampersand should be URL-encoded");
    }

    // ── Capitation Service Contracts ──────────────────────────────

    [Fact]
    public async Task CapitationService_GetContracts_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new CapitationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://capitation-service") },
            config, Mock.Of<ILogger<CapitationService>>());

        await sut.GetContractsAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/v1/capitation/contracts");
    }

    [Fact]
    public async Task CapitationService_GetRuns_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new CapitationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://capitation-service") },
            config, Mock.Of<ILogger<CapitationService>>());

        await sut.GetRunsAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/v1/capitation/runs");
    }

    [Fact]
    public async Task CapitationService_GetUnpaidStatements_CallsCorrectUrl()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new CapitationService(
            new HttpClient(handler) { BaseAddress = new Uri("http://capitation-service") },
            config, Mock.Of<ILogger<CapitationService>>());

        await sut.GetUnpaidStatementsAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/v1/capitation/statements/unpaid");
    }

    // ── Work Queue Contracts ──────────────────────────────────────

    [Fact]
    public async Task WorkQueueService_GetSummary_CallsClaimsServiceWorkQueue()
    {
        var handler = new RecordingHandler(body: """{"ncciEditFailures":0,"missingAuth":0,"providerNotContracted":0,"cobRequired":0,"medicalReview":0}""");
        var config = BuildConfig(new());
        var sut = new WorkQueueService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<WorkQueueService>>());

        await sut.GetQueueSummaryAsync();

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().Be("/api/Claims/work-queue/summary");
    }

    [Fact]
    public async Task WorkQueueService_GetItems_CallsClaimsServiceWorkQueue()
    {
        var handler = new RecordingHandler();
        var config = BuildConfig(new());
        var sut = new WorkQueueService(
            new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") },
            config, Mock.Of<ILogger<WorkQueueService>>());

        await sut.GetQueueItemsAsync(limit: 50);

        handler.RecordedPaths.Should().ContainSingle()
            .Which.Should().StartWith("/api/Claims/work-queue/items")
            .And.Subject.Should().Contain("limit=50");
    }
}
