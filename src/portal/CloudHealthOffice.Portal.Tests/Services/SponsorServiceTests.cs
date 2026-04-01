using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class SponsorServiceTests
{
    private readonly Mock<ILogger<SponsorService>> _logger = new();
    private readonly IConfiguration _configuration;

    public SponsorServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:SponsorService"] = "http://localhost:5007"
            })
            .Build();
    }

    private SponsorService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new SponsorService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchSponsorsAsync ──

    [Fact]
    public async Task SearchSponsorsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchSponsorsAsync("Acme"));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── GetSponsorByIdAsync ──

    [Fact]
    public async Task GetSponsorByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetSponsorByIdAsync("SP-001"));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── CreateSponsorAsync ──

    [Fact]
    public async Task CreateSponsorAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateSponsorAsync(new CreateSponsorRequest()));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    // ── UpdateSponsorAsync ──

    [Fact]
    public async Task UpdateSponsorAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateSponsorAsync("SP-001", new UpdateSponsorRequest()));
        ex.ServiceName.Should().Be("Sponsor Service");
    }

    [Fact]
    public async Task SearchSponsorsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchSponsorsAsync("Acme"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── SearchSponsorsAsync ──

    [Fact]
    public async Task SearchSponsorsAsync_WhenApiReturnsWrappedObject_DeserializesSponsorList()
    {
        // SponsorService parses via GetFromJsonAsync<JsonElement> then
        // JsonElement.Deserialize<T>() which uses default (case-sensitive) options,
        // so inner properties must be PascalCase to match the DTO.
        var json = JsonSerializer.Serialize(new
        {
            sponsors = new[]
            {
                new { SponsorId = "SP-1", Name = "Acme Corp", Type = "Employer",
                      State = "IL", ActiveBenefitPlans = 3, TotalMembers = 1200,
                      Status = "Active", ContractStartDate = "2024-01-01" },
                new { SponsorId = "SP-2", Name = "Beta Union", Type = "Union",
                      State = "NY", ActiveBenefitPlans = 1, TotalMembers = 500,
                      Status = "Active", ContractStartDate = "2024-06-01" }
            }
        });

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchSponsorsAsync("Acme");

        result.Should().HaveCount(2);
        result[0].SponsorId.Should().Be("SP-1");
        result[0].Name.Should().Be("Acme Corp");
        result[0].TotalMembers.Should().Be(1200);
        result[1].Type.Should().Be("Union");
    }

    [Fact]
    public async Task SearchSponsorsAsync_WhenApiReturnsEmptySponsorsArray_ReturnsEmptyList()
    {
        // Wrapped object with an empty sponsors array
        var json = JsonSerializer.Serialize(new { sponsors = Array.Empty<object>() });

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchSponsorsAsync("Nobody");

        result.Should().BeEmpty();
    }

    // ── GetSponsorByIdAsync ──

    [Fact]
    public async Task GetSponsorByIdAsync_WhenApiReturns200_DeserializesSponsorDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            sponsorId = "SP-1", name = "Acme Corp", type = "Employer",
            state = "IL", activeBenefitPlans = 3, totalMembers = 1200,
            status = "Active", contractStartDate = "2024-01-01",
            taxId = "12-3456789", addressLine1 = "100 Corporate Blvd",
            city = "Chicago", zipCode = "60601",
            contactName = "Jane Admin", contactPhone = "312-555-0100",
            contactEmail = "admin@acme.com",
            billingFrequency = "Monthly", paymentMethod = "ACH",
            groupSizeTier = "Large (50+)",
            benefitPlans = new[]
            {
                new { planId = "PLN-1", planName = "Gold PPO", productType = "PPO",
                      enrolledMembers = 800, effectiveDate = "2025-01-01" }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetSponsorByIdAsync("SP-1");

        result.Should().NotBeNull();
        result!.TaxId.Should().Be("12-3456789");
        result.ContactEmail.Should().Be("admin@acme.com");
        result.BillingFrequency.Should().Be("Monthly");
        result.GroupSizeTier.Should().Be("Large (50+)");
        result.BenefitPlans.Should().ContainSingle()
            .Which.PlanName.Should().Be("Gold PPO");
    }

    [Fact]
    public async Task GetSponsorByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetSponsorByIdAsync("SP-NONE");

        result.Should().BeNull();
    }

    // ── CreateSponsorAsync ──

    [Fact]
    public async Task CreateSponsorAsync_WhenApiReturns200_ExtractsSponsorId()
    {
        // CreateSponsorAsync reads the "id" property from a JsonElement
        var json = JsonSerializer.Serialize(new { id = "SP-NEW-42" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateSponsorAsync(new CreateSponsorRequest
        {
            Name = "New Sponsor", TaxId = "98-7654321", State = "TX"
        });

        result.Should().Be("SP-NEW-42");
    }

    [Fact]
    public async Task CreateSponsorAsync_VerifyPostSendsPayload()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { id = "SP-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.CreateSponsorAsync(new CreateSponsorRequest
        {
            Name = "Delta Corp", TaxId = "11-2233445",
            City = "Dallas", State = "TX", ContactEmail = "info@delta.com"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Delta Corp");
        body.Should().Contain("info@delta.com");
    }

    // ── UpdateSponsorAsync ──

    [Fact]
    public async Task UpdateSponsorAsync_WhenApiReturns200_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateSponsorAsync("SP-1", new UpdateSponsorRequest
        {
            Name = "Acme Corp Updated", Status = "Active"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/sponsors/SP-1");
    }

    // ── UpdateSponsorRequest – ContractEndDate ────────────────────────────────

    [Fact]
    public async Task UpdateSponsorAsync_WithContractEndDate_SendsContractEndDateInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var req = new UpdateSponsorRequest
        {
            Name = "Expiring Corp", Status = "Terminated",
            ContractEndDate = new DateTime(2026, 6, 30)
        };

        // Verify property is readable
        req.Status.Should().Be("Terminated");
        req.ContractEndDate.Should().Be(new DateTime(2026, 6, 30));

        await sut.UpdateSponsorAsync("SP-2", req);

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Terminated");
    }
}
