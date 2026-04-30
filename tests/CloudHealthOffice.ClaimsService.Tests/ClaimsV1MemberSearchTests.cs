using System.Net;
using System.Text.Json;
using ClaimsService.Adapters;
using ClaimsService.Fhir;
using ClaimsService.Models;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// v1 member-scoped claims surface — covers the /api/v1/claims route,
/// adapter-routed reads (capability 5.3 migrated this from
/// IClaimRepository.SearchForMemberAsync), filter pass-through, and
/// the FHIR EOB projection produced by
/// <see cref="ExplanationOfBenefitProjector"/>.
/// </summary>
public class ClaimsV1MemberSearchTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimAdapter _adapter;

    public ClaimsV1MemberSearchTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _adapter = factory.ClaimAdapter;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        // Reset the shared adapter substitute between tests since the
        // factory is shared across the class fixture. ClearSubstitute()
        // clears both received calls AND configured returns; we re-establish
        // Platform="cho" because ClaimAdapterFactory routes by it.
        _adapter.ClearSubstitute();
        _adapter.Platform.Returns("cho");
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
        _adapter
            .SearchClaimsForMemberAsync(
                Arg.Is<ClaimMemberSearchAdapterRequest>(r => r.MemberId == "MEM-42"),
                Arg.Any<CancellationToken>())
            .Returns(new ClaimSearchAdapterResponse
            {
                Platform = "cho",
                Claims = new[] { AdapterClaim.From(claim) },
                TotalCount = 1,
            });

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
        _adapter
            .SearchClaimsForMemberAsync(
                Arg.Any<ClaimMemberSearchAdapterRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ClaimSearchAdapterResponse
            {
                Platform = "cho",
                Claims = Array.Empty<AdapterClaim>(),
                TotalCount = 0,
            });

        var response = await _client.GetAsync(
            "/api/v1/claims?memberId=MEM-99&amountMin=100&amountMax=500&claimType=Institutional&status=Paid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _adapter.Received(1).SearchClaimsForMemberAsync(
            Arg.Is<ClaimMemberSearchAdapterRequest>(r =>
                r.MemberId == "MEM-99" &&
                r.AmountMin == 100m &&
                r.AmountMax == 500m &&
                r.ClaimType == ClaimType.Institutional &&
                r.Status == ClaimStatus.Paid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemberClaims_NullTotalCount_FallsBackToPageSize()
    {
        // Defensive fallback for adapters that don't surface a total
        // (vendor stubs would, in theory, though they currently throw
        // NotImplementedException before this path).
        var claims = new[]
        {
            AdapterClaim.From(BuildClaim("MEM-50", "CLM-B1")),
            AdapterClaim.From(BuildClaim("MEM-50", "CLM-B2")),
        };
        _adapter
            .SearchClaimsForMemberAsync(
                Arg.Any<ClaimMemberSearchAdapterRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ClaimSearchAdapterResponse
            {
                Platform = "cho",
                Claims = claims,
                TotalCount = null,
            });

        var response = await _client.GetAsync("/api/v1/claims?memberId=MEM-50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
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
