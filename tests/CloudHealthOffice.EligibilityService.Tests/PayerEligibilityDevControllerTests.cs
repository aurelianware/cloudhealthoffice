using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class PayerEligibilityDevControllerTests : IClassFixture<EligibilityApiFactory>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PayerEligibilityDevControllerTests(EligibilityApiFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "untrusted-tenant");
    }

    [Fact]
    public async Task CanonicalInquiry_ReturnsActiveCoverage()
    {
        var inquiry = new
        {
            transactionId = "dev-txn-1",
            correlationId = "dev-corr-1",
            payerId = ChoDemoEligibilitySeed.ExternalPayerId,
            claimedTenantId = "untrusted-tenant",
            requestingProvider = new { npi = ChoDemoEligibilitySeed.InNetworkNpi, organizationName = "ACME Health Services" },
            subscriber = new
            {
                memberId = ChoDemoEligibilitySeed.SubscriberMemberId,
                firstName = ChoDemoEligibilitySeed.SubscriberFirstName,
                lastName = ChoDemoEligibilitySeed.SubscriberLastName,
                dateOfBirth = "1980-01-15",
                relationshipToSubscriber = "self"
            },
            serviceTypeCodes = new[] { "30" },
            dateOfService = "2026-08-23"
        };

        var response = await _client.PostAsJsonAsync("/api/dev/payer/eligibility", inquiry, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("isSuccess").GetBoolean());
        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("isEligible").GetBoolean());
        Assert.Equal("Success", result.GetProperty("businessStatus").GetString());
        Assert.Equal("Active", result.GetProperty("coverageStatus").GetString());
        Assert.Equal(ChoDemoEligibilitySeed.TenantId, result.GetProperty("tenantId").GetString());
        Assert.Equal("Demo PPO", result.GetProperty("planName").GetString());
        Assert.Equal(800m, result.GetProperty("deductible").GetProperty("individualRemaining").GetDecimal());
        Assert.Equal(3200m, result.GetProperty("outOfPocket").GetProperty("individualRemaining").GetDecimal());
        Assert.Equal(
            nameof(GatewayTransactionStatus.Completed),
            root.GetProperty("metadata").GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidSubscriber_ReturnsHttp200WithBusinessRejection()
    {
        var inquiry = new
        {
            payerId = ChoDemoEligibilitySeed.ExternalPayerId,
            subscriber = new
            {
                memberId = "NO-SUCH-MEMBER",
                firstName = "Nobody",
                lastName = "Here",
                dateOfBirth = "1999-09-09"
            },
            serviceTypeCodes = new[] { "30" },
            dateOfService = "2026-08-23"
        };

        var response = await _client.PostAsJsonAsync("/api/dev/payer/eligibility", inquiry, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(
            nameof(EligibilityBusinessStatus.SubscriberNotFound),
            doc.RootElement.GetProperty("result").GetProperty("businessStatus").GetString());
        Assert.Equal(
            nameof(GatewayTransactionStatus.Rejected),
            doc.RootElement.GetProperty("metadata").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OmittedDateOfService_ReturnsInvalidDate()
    {
        var inquiry = new
        {
            payerId = ChoDemoEligibilitySeed.ExternalPayerId,
            subscriber = new
            {
                memberId = ChoDemoEligibilitySeed.SubscriberMemberId,
                firstName = ChoDemoEligibilitySeed.SubscriberFirstName,
                lastName = ChoDemoEligibilitySeed.SubscriberLastName,
                dateOfBirth = "1980-01-15"
            },
            serviceTypeCodes = new[] { "30" }
        };

        var response = await _client.PostAsJsonAsync("/api/dev/payer/eligibility", inquiry, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(EligibilityBusinessStatus.InvalidDate),
            doc.RootElement.GetProperty("result").GetProperty("businessStatus").GetString());
    }
}
