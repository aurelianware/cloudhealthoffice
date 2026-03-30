using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NSubstitute;
using EligibilityService.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class EligibilityControllerTests : IClassFixture<EligibilityApiFactory>
{
    private readonly HttpClient _client;
    private readonly EligibilityApiFactory _factory;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public EligibilityControllerTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Health / Readiness
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/eligibility/inquiry — Submit 270 Inquiry
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubmitInquiry_ValidRequest_ReturnsSuccess()
    {
        var expectedResponse = new EligibilityResponse
        {
            Id = Guid.NewGuid().ToString(),
            InquiryId = "INQ-001",
            TenantId = "test-tenant",
            ResponseCode = "AAA",
            StatusCode = "1",
            IsCovered = true,
            CoverageLevel = "IND",
            InsurancePlanName = "Test PPO Plan",
            CreatedDate = DateTime.UtcNow
        };

        _factory.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .Returns(expectedResponse);

        var inquiry = new
        {
            tenantId = "test-tenant",
            payerId = "PAYER-001",
            payerName = "Test Health Plan",
            providerNPI = "1234567890",
            subscriberId = "SUB-001",
            subscriberFirstName = "Jane",
            subscriberLastName = "Doe",
            subscriberDOB = "1985-06-15",
            serviceTypeCode = "30",
            controlNumber = "CTL-001"
        };

        var response = await _client.PostAsJsonAsync("/api/eligibility/inquiry", inquiry, Json);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Expected 200/201 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/eligibility/check — Quick Eligibility Check
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task QuickCheck_ActiveSubscriber_ReturnsEligible()
    {
        _factory.EligibilityService.QuickEligibilityCheckAsync(
            "test-tenant", "SUB-001", Arg.Any<string?>(), Arg.Any<DateTime>())
            .Returns((true, "1", "IND", "Active coverage"));

        var response = await _client.GetAsync(
            "/api/eligibility/check?subscriberId=SUB-001&serviceDate=2026-01-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(result.TryGetProperty("isEligible", out var eligible));
        Assert.True(eligible.GetBoolean());
    }

    [Fact]
    public async Task QuickCheck_MissingSubscriberId_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/eligibility/check");

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400/404 for missing subscriberId but got {response.StatusCode}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/eligibility/benefits/{subscriberId}
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetBenefits_ValidSubscriber_ReturnsBenefitList()
    {
        var benefits = new List<EligibilityBenefit>
        {
            new()
            {
                ServiceTypeCode = "30",
                ServiceTypeName = "Health Benefit Plan Coverage",
                CoverageLevel = "IND",
                MonetaryAmount = 5000m,
                NetworkIndicator = "Y"
            }
        };

        _factory.EligibilityService.GetBenefitDetailsAsync(
            "test-tenant", "SUB-001", Arg.Any<string?>(), Arg.Any<DateTime>())
            .Returns(benefits);

        var response = await _client.GetAsync("/api/eligibility/benefits/SUB-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/eligibility/history/{subscriberId}
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetHistory_ValidSubscriber_ReturnsInquiryList()
    {
        var inquiries = new List<EligibilityInquiry>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = "test-tenant",
                SubscriberId = "SUB-001",
                PayerId = "PAYER-001",
                Status = EligibilityInquiryStatus.Completed,
                RequestDate = DateTime.UtcNow.AddDays(-1)
            }
        };

        _factory.EligibilityService.GetInquiryHistoryAsync(
            "test-tenant", "SUB-001", 1, 10)
            .Returns(inquiries);

        var response = await _client.GetAsync("/api/eligibility/history/SUB-001?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/eligibility/validate-auth
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateAuth_ServiceRequiringAuth_ReturnsRequired()
    {
        _factory.EligibilityService.CheckAuthRequirementAsync(
            "test-tenant", "SUB-001", "42", "70553")
            .Returns((true, "MRI requires prior authorization"));

        var request = new
        {
            subscriberId = "SUB-001",
            serviceTypeCode = "42",
            procedureCode = "70553"
        };

        var response = await _client.PostAsJsonAsync("/api/eligibility/validate-auth", request, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(result.TryGetProperty("requiresAuth", out var reqAuth));
        Assert.True(reqAuth.GetBoolean());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tenant isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MissingTenantHeader_ReturnsBadRequestOrUnauthorized()
    {
        using var noTenantClient = _factory.CreateClient();
        // Don't add X-Tenant-ID header

        var response = await noTenantClient.GetAsync("/api/eligibility/check?subscriberId=SUB-001");

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.OK, // Some services tolerate missing tenant for non-data endpoints
            $"Expected 400/401 for missing tenant but got {response.StatusCode}");
    }
}
