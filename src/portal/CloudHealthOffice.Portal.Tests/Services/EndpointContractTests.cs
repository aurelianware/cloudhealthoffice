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
            .Which.Should().Be("/api/work-queue/summary");
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
            .Which.Should().StartWith("/api/work-queue/items")
            .And.Subject.Should().Contain("limit=50");
    }
}
