using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// v1 member-scoped claims surface — covers the /api/v1/claims route,
/// amountRange/claimType filter pass-through, and the FHIR EOB projection
/// produced by <see cref="ExplanationOfBenefitProjector"/>.
/// </summary>
public class ClaimsV1MemberSearchTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimRepository _repo;

    public ClaimsV1MemberSearchTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    [Fact]
    public async Task SearchMemberClaims_WithoutMemberId_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/claims");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchMemberClaims_ReturnsEobWrapperWithProjectedResources()
    {
        var claim = BuildClaim("MEM-42", "CLM-A1");
        _repo.SearchForMemberAsync(
                "MEM-42",
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<ClaimStatus?>(),
                Arg.Any<string?>(), Arg.Any<ClaimType?>(),
                Arg.Any<decimal?>(), Arg.Any<decimal?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns((new List<Claim> { claim }, 1));

        var response = await _client.GetAsync("/api/v1/claims?memberId=MEM-42&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.Equal(5, root.GetProperty("pageSize").GetInt32());

        var resources = root.GetProperty("resources");
        Assert.Equal(1, resources.GetArrayLength());
        var eob = resources[0];
        Assert.Equal("ExplanationOfBenefit", eob.GetProperty("resourceType").GetString());
        Assert.Equal("Patient/MEM-42", eob.GetProperty("patient").GetProperty("reference").GetString());
        Assert.Equal("CLM-A1", eob.GetProperty("identifier")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task SearchMemberClaims_ForwardsAmountAndClaimTypeFilters()
    {
        _repo.SearchForMemberAsync(
                "MEM-99",
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<ClaimStatus?>(),
                Arg.Any<string?>(), Arg.Any<ClaimType?>(),
                Arg.Any<decimal?>(), Arg.Any<decimal?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns((new List<Claim>(), 0));

        var response = await _client.GetAsync(
            "/api/v1/claims?memberId=MEM-99&amountMin=100&amountMax=500&claimType=Institutional&status=Paid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _repo.Received(1).SearchForMemberAsync(
            "MEM-99",
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            ClaimStatus.Paid,
            Arg.Any<string?>(),
            ClaimType.Institutional,
            100m, 500m,
            Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void Projector_MapsTerminalStatusesToComplete()
    {
        var projector = new ExplanationOfBenefitProjector();
        var claim = BuildClaim("MEM-7", "CLM-P");
        claim.Status = ClaimStatus.Paid;

        var eob = projector.Project(claim);
        Assert.Equal("complete", (string?)eob["outcome"]);
        Assert.Equal("active", (string?)eob["status"]);
    }

    [Fact]
    public void Projector_MapsDraftStatusesToQueued()
    {
        var projector = new ExplanationOfBenefitProjector();
        var claim = BuildClaim("MEM-8", "CLM-D");
        claim.Status = ClaimStatus.Pended;

        var eob = projector.Project(claim);
        Assert.Equal("queued", (string?)eob["outcome"]);
        Assert.Equal("draft", (string?)eob["status"]);
    }

    private static Claim BuildClaim(string memberId, string claimNumber) => new()
    {
        TenantId = "test-tenant",
        Id = Guid.NewGuid().ToString(),
        ClaimNumber = claimNumber,
        MemberId = memberId,
        BillingProviderNPI = "1234567890",
        BillingProviderName = "Test Clinic",
        LineOfBusiness = LineOfBusiness.Commercial,
        ClaimType = ClaimType.Professional,
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 250.00m,
        ServiceDateFrom = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = ClaimStatus.Submitted,
        ClaimLines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ChargeAmount = 250.00m,
                Units = 1,
                ServiceDateFrom = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        }
    };
}
